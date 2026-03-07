using System;
using System.Collections.Generic;

namespace HIS_RIS_Integration
{
    public class TestOrder
    {
        public long? PatientId { get; set; }
        public string? FirstName { get; set; }
        public string? LastName { get; set; }
        public DateTime? DateOfBirth { get; set; }
        public string? Gender { get; set; }
        public string? Phone { get; set; }
        public int? PhysicianID { get; set; }
        public string? Physician { get; set; }
        public long? AdmissionId { get; set; }
        public long? BillId { get; set; }
        public string? BillStatus { get; set; }
        public List<TestOrderItem> TestOrderItems { get; set; } = new();
    }

    public class TestOrderItem
    {
        public long? OrderId { get; set; }
        public long? TestId { get; set; }
        public string? TestName { get; set; }
        public string? AE_Title { get; set; }
        public string? Modality { get; set; }
        public double? AfterRefund { get; set; }
    }

    public class TestReport 
    {
        public int? EventId { get; set; }
        public int? OrderId { get; set; }
        public DateTime? ReportTime { get; set; }
        public string? ReportStatus { get; set; }
        public string? Report { get; set; }
        public string? PACS_URL { get; set; }
    }
}
