namespace ScanHttpServer
{
    public class ScanResults
    {
        public string FileName { get; set; }
        public bool IsThreat { get; set; } = false;
        public string ThreatType { get; set; }
        public bool IsError { get; set; } = false;
        public string ErrorMessage { get; set; }
    }
}