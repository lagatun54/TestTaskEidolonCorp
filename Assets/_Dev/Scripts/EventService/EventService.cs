using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;
using Cysharp.Threading.Tasks;
using System.Threading;

namespace Analytics
{
    public class EventService : MonoBehaviour
    {
        #region Public Properties
        
        public string serverUrl = "https://analytics.example.com/api/events";
        
        [Range(1, 10)]
        public int cooldownBeforeSend = 8;
        
        [Range(1, 10)]
        public int maxRetryAttempts = 5;
        
        [Range(1, 30)]
        public int retryDelay = 5;
        
        [Range(0, 20)]
        public int initialRequestsCount = 10;

        #endregion

        #region Singleton

        public static EventService Instance { get; private set; }

        #endregion

        #region Private Fields

        private readonly Queue<ICommand> _commandQueue = new Queue<ICommand>();
        private readonly List<AnalyticEvent> _pendingEvents = new List<AnalyticEvent>();
        private readonly List<AnalyticEvent> _persistedEvents = new List<AnalyticEvent>();
        
        private CancellationTokenSource _cooldownCancellationTokenSource;
        private CancellationTokenSource _sendEventsCancellationTokenSource;
        
        private bool _isCooldownActive;
        private bool _isSending;
        
        private string _persistenceFilePath;

        #endregion

        #region Unity Lifecycle

        void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Debug.LogWarning("[EventService] Duplicate EventService instance found. Destroying this one.");
                Destroy(gameObject);
                return;
            }

            Instance = this;
            
            _persistenceFilePath = Path.Combine(Application.persistentDataPath, "analytics_events.json");
            
            LoadPersistedEvents();
            
            DontDestroyOnLoad(gameObject);
        }

        void Start()
        {
            if (_persistedEvents.Count > 0)
            {
                Debug.Log($"[EventService] Found {_persistedEvents.Count} persisted events. Starting send process.");
                _pendingEvents.AddRange(_persistedEvents);
                _persistedEvents.Clear();
                StartSendProcess();
            }
            
            for (int i = 0; i < initialRequestsCount; i++)
            {
                TrackEvent("appStart", $"session_id:{Guid.NewGuid()}");
            }
        }

        void Update()
        {
            ProcessCommandQueue();
        }

        void OnApplicationPause(bool pauseStatus)
        {
            if (pauseStatus)
            {
                SavePendingEvents();
            }
        }

        void OnApplicationFocus(bool hasFocus)
        {
            if (!hasFocus)
            {
                SavePendingEvents();
            }
        }

        void OnDestroy()
        {
            SavePendingEvents();
        }

        #endregion

        #region Public API

        public void TrackEvent(string type, string data)
        {
            if (string.IsNullOrEmpty(type))
            {
                Debug.LogWarning("[EventService] Event type cannot be null or empty");
                return;
            }
            
            var command = new TrackEventCommand(this, type, data);
            _commandQueue.Enqueue(command);
        }

        #endregion

        #region Command Processing

        private void ProcessCommandQueue()
        {
            while (_commandQueue.Count > 0)
            {
                var command = _commandQueue.Dequeue();
                command.Execute();
            }
        }

        #endregion

        #region Event Processing

        internal void AddEvent(string type, string data)
        {
            var analyticEvent = new AnalyticEvent(type, data);
            _pendingEvents.Add(analyticEvent);
            
            Debug.Log($"[EventService] Event added: {type} = {data}. Pending events: {_pendingEvents.Count}");
            
            if (!_isCooldownActive)
            {
                StartCooldown();
            }
        }

        private void StartCooldown()
        {
            _isCooldownActive = true;
            _cooldownCancellationTokenSource = new CancellationTokenSource();
            CooldownCoroutine(_cooldownCancellationTokenSource.Token).Forget();
        }

        private async UniTask CooldownCoroutine(CancellationToken cancellationToken)
        {
            Debug.Log($"[EventService] Cooldown started for {cooldownBeforeSend} seconds");
            await UniTask.Delay(TimeSpan.FromSeconds((double)cooldownBeforeSend), cancellationToken: cancellationToken);
            
            if (cancellationToken.IsCancellationRequested)
            {
                Debug.Log("[EventService] Cooldown cancelled.");
                return;
            }
            
            Debug.Log($"[EventService] Cooldown finished. Starting send process for {_pendingEvents.Count} events");
            StartSendProcess();
        }

        public void StartSendProcess()
        {
            if (_isSending || _pendingEvents.Count == 0)
                return;
                
            _isSending = true;
            _isCooldownActive = false;
            
            if (_cooldownCancellationTokenSource != null)
            {
                _cooldownCancellationTokenSource.Cancel();
                _cooldownCancellationTokenSource.Dispose();
                _cooldownCancellationTokenSource = null;
            }
            
            if (_sendEventsCancellationTokenSource != null)
            {
                _sendEventsCancellationTokenSource.Cancel();
                _sendEventsCancellationTokenSource.Dispose();
            }
            
            _sendEventsCancellationTokenSource = new CancellationTokenSource();
            SendEventsCoroutine(_sendEventsCancellationTokenSource.Token).Forget();
        }

        #endregion

        #region HTTP Client

        private async UniTask SendEventsCoroutine(CancellationToken cancellationToken)
        {
            var eventsToSend = new List<AnalyticEvent>(_pendingEvents);
            var attempts = 0;
            
            while (attempts < maxRetryAttempts)
            {
                attempts++;
                Debug.Log($"[EventService] Sending {eventsToSend.Count} events, attempt {attempts}/{maxRetryAttempts}");
                
                var request = CreateHttpRequest(eventsToSend);
                await request.SendWebRequest().ToUniTask(cancellationToken: cancellationToken);
                
                if (cancellationToken.IsCancellationRequested)
                {
                    Debug.Log("[EventService] Send events cancelled.");
                    _isSending = false;
                    return;
                }
                
                if (request.result == UnityWebRequest.Result.Success && request.responseCode == 200)
                {
                    Debug.Log($"[EventService] Successfully sent {eventsToSend.Count} events with 200 OK response.");
                    
                    foreach (var sentEvent in eventsToSend)
                    {
                        _pendingEvents.Remove(sentEvent);
                    }
                    
                    _isSending = false;
                    
                    if (_pendingEvents.Count > 0)
                    {
                        StartCooldown();
                    }
                    
                    return;
                }
                else
                {
                    Debug.LogError($"[EventService] Failed to send events. Result: {request.result}, Response Code: {request.responseCode}, Error: {request.error}");
                    
                    if (attempts < maxRetryAttempts)
                    {
                        Debug.Log($"[EventService] Retrying in {retryDelay} seconds...");
                        await UniTask.Delay(TimeSpan.FromSeconds((double)retryDelay), cancellationToken: cancellationToken);
                        
                        if (cancellationToken.IsCancellationRequested)
                        {
                            Debug.Log("[EventService] Send events retry cancelled.");
                            _isSending = false;
                            return;
                        }
                    }
                }
            }
            
            Debug.LogError($"[EventService] Failed to send events after {maxRetryAttempts} attempts. Events will be persisted.");
            _isSending = false;
            
            SavePendingEvents();
        }

        private UnityWebRequest CreateHttpRequest(List<AnalyticEvent> events)
        {
            var payload = new EventsPayload { events = events };
            var json = JsonUtility.ToJson(payload);
            
            Debug.Log($"[EventService] Sending JSON Payload:\n{json}");

            var jsonBytes = Encoding.UTF8.GetBytes(json);
            
            var request = new UnityWebRequest(serverUrl, "POST");
            request.uploadHandler = new UploadHandlerRaw(jsonBytes);
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");
            
            return request;
        }

        #endregion

        #region Persistence

        private void LoadPersistedEvents()
        {
            try
            {
                if (File.Exists(_persistenceFilePath))
                {
                    var json = File.ReadAllText(_persistenceFilePath);
                    if (!string.IsNullOrEmpty(json))
                    {
                        var payload = JsonUtility.FromJson<EventsPayload>(json);
                        if (payload?.events != null)
                        {
                            _persistedEvents.AddRange(payload.events);
                            Debug.Log($"[EventService] Loaded {_persistedEvents.Count} persisted events");
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[EventService] Failed to load persisted events: {e.Message}");
            }
        }

        private void SavePendingEvents()
        {
            try
            {
                var payload = new EventsPayload { events = new List<AnalyticEvent>(_pendingEvents) };
                var json = JsonUtility.ToJson(payload, true);
                File.WriteAllText(_persistenceFilePath, json);
                
                Debug.Log($"[EventService] Saved {_pendingEvents.Count} pending events to persistence");
            }
            catch (Exception e)
            {
                Debug.LogError($"[EventService] Failed to save pending events: {e.Message}");
            }
        }

        #endregion

        #region Debug Methods

        [ContextMenu("Test Event - Level Start")]
        private void TestLevelStart()
        {
            TrackEvent("levelStart", "level:3");
        }

        [ContextMenu("Test Event - Reward Received")]
        private void TestRewardReceived()
        {
            TrackEvent("rewardReceived", "coins:100");
        }

        [ContextMenu("Test Event - Currency Spent")]
        private void TestCurrencySpent()
        {
            TrackEvent("currencySpent", "item:sword,cost:50");
        }

        [ContextMenu("Force Save Events")]
        private void ForceSaveEvents()
        {
            SavePendingEvents();
            Debug.Log("[EventService] Events forcibly saved");
        }

        #endregion
    }
}