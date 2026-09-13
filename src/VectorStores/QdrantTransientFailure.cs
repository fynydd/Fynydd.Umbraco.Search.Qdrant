using System.Net.Sockets;
using Grpc.Core;

namespace Fynydd.Umbraco.Search.Qdrant.VectorStores;

/// <summary>
/// Identifies temporary Qdrant connectivity failures that should not stop Umbraco.
/// </summary>
internal static class QdrantTransientFailure
{
    /// <summary>
    /// Determines whether an exception represents temporary Qdrant unavailability.
    /// </summary>
    public static bool IsTransient(Exception exception)
    {
        if (exception is RpcException { StatusCode: StatusCode.Unavailable or StatusCode.DeadlineExceeded or StatusCode.Cancelled })
            return true;

        if (exception is OperationCanceledException or TimeoutException or HttpRequestException or SocketException)
            return true;

        return exception.InnerException is not null && IsTransient(exception.InnerException);
    }
}
