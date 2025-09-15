namespace Analytics
{

    public class TrackEventCommand : ICommand
    {
        private readonly EventService _eventService;
        private readonly string _type;
        private readonly string _data;

        public TrackEventCommand(EventService eventService, string type, string data)
        {
            _eventService = eventService;
            _type = type;
            _data = data;
        }

        public void Execute()
        {
            _eventService.AddEvent(_type, _data);
        }
    }

    public class SendEventsCommand : ICommand
    {
        private readonly EventService _eventService;

        public SendEventsCommand(EventService eventService)
        {
            _eventService = eventService;
        }

        public void Execute()
        {
            _eventService.StartSendProcess();
        }
    }
}

