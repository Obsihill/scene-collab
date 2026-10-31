namespace SceneCollab.Domain.Room
{
    /// <summary>
    /// Outcome of <see cref="RoomState.Apply"/>. A rejection always carries the current
    /// revision so the client can resync without an extra round trip.
    /// </summary>
    public sealed class ApplyResult
    {
        public bool Accepted;
        public bool Duplicate;
        public string Code;
        public string Message;
        public long Revision;
        public OpRecord Record;

        public static ApplyResult Accept(OpRecord record)
        {
            return new ApplyResult
            {
                Accepted = true,
                Duplicate = false,
                Revision = record.Revision,
                Record = record,
            };
        }

        public static ApplyResult AcceptDuplicate(string opId, long revision, OpRecord record)
        {
            return new ApplyResult
            {
                Accepted = true,
                Duplicate = true,
                Revision = revision,
                Record = record,
            };
        }

        public static ApplyResult Reject(string code, long currentRevision, string message)
        {
            return new ApplyResult
            {
                Accepted = false,
                Duplicate = false,
                Code = code,
                Message = message,
                Revision = currentRevision,
            };
        }
    }
}
