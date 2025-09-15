using System;
using System.Collections.Generic;

namespace Analytics
{
    [Serializable]
    public class AnalyticEvent
    {
        public string type;
        public string data;
        
        public AnalyticEvent(string eventType, string eventData)
        {
            type = eventType ?? string.Empty;
            data = eventData ?? string.Empty;
        }
    }

    [Serializable]
    public class EventsPayload
    {
        public List<AnalyticEvent> events;
    }
}
