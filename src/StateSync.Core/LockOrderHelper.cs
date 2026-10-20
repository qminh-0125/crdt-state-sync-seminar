using System;
using System.Threading;

namespace StateSync.Core;

/// <summary>
/// Bộ điều phối thứ tự khóa toàn cục (Canonical Lock Ordering) tránh vòng chờ giữa hai tập
/// khi hai luồng thực hiện Merge hoặc Compare ngược chiều (A.Merge(B) song song với B.Merge(A)),
/// trong các phương thức Merge/Compare. Khóa nội bộ không phải API để caller tự khóa.
/// </summary>
internal static class LockOrderHelper
{
    private static long _nextId;

    public static long NextInstanceId() => Interlocked.Increment(ref _nextId);

    public static void ExecuteLocked(
        object thisLock,
        long thisId,
        object otherLock,
        long otherId,
        Action action)
    {
        if (ReferenceEquals(thisLock, otherLock))
        {
            lock (thisLock)
            {
                action();
            }
            return;
        }

        // Never release a lock owned by an outer caller: that would violate its
        // critical section. Public methods enter here without holding either lock.
        AcquireInCanonicalOrder(thisLock, thisId, otherLock, otherId, action);
    }

    public static TResult ExecuteLocked<TResult>(
        object thisLock,
        long thisId,
        object otherLock,
        long otherId,
        Func<TResult> func)
    {
        TResult result = default!;
        ExecuteLocked(thisLock, thisId, otherLock, otherId, () =>
        {
            result = func();
        });
        return result;
    }

    private static void AcquireInCanonicalOrder(
        object thisLock,
        long thisId,
        object otherLock,
        long otherId,
        Action action)
    {
        object first = thisId < otherId ? thisLock : otherLock;
        object second = thisId < otherId ? otherLock : thisLock;

        lock (first)
        {
            lock (second)
            {
                action();
            }
        }
    }
}
