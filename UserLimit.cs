namespace EmbyUserControl
{
    public class UserLimit
    {
        public string Username { get; set; }
        public int LimitMinutes { get; set; }
        public string AllowedStartTime { get; set; }
        public string AllowedEndTime { get; set; }
        public string CutoffTime { get; set; }
    }
}
