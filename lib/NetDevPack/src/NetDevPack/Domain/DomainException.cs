using System;

namespace NetDevPack.Domain
{
    /// <summary>
    /// Represents errors that occur within the domain layer of an application.
    /// </summary>
    public class DomainException : Exception
    {
        public DomainException()
        { }

        public DomainException(string message) : base(message)
        { }

        public DomainException(string message, Exception innerException) : base(message, innerException)
        { }
    }
}