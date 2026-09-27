// ThumbnailLimiter.cs
// d2: 이 포크는 ThumbViewerItem 안에서 static SemaphoreSlim(1, 1) 로 썸네일 읽기를
//     전부 직렬화하고 있었다. 그래서 16코어에서도 원본 디코딩이 한 번에 하나씩만 돌았고,
//     25MB PNG(56MP) 한 장에 400ms 가 걸리는 폴더는 사실상 순차 처리됐다.
//
//     그렇다고 무제한으로 병렬화하면 56MP 한 장이 디코딩 중에만 수백 MB 를 쓰기 때문에
//     금방 메모리를 말아먹는다. 그래서 "동시 작업 수" 가 아니라 "동시에 디코딩 중인 픽셀량"
//     을 기준으로 제한한다. 큰 이미지는 혼자(또는 둘씩) 돌고, 보통 크기 사진은 코어 수만큼
//     병렬로 돈다.

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ZipPla
{
    /// <summary>가중치 기반 동시 실행 제한기. 작업마다 필요한 자원량을 미리 잡고 들어간다.</summary>
    internal sealed class WeightedThrottle
    {
        private readonly object locker = new object();
        private readonly System.Collections.Generic.Queue<Waiter> waiters = new System.Collections.Generic.Queue<Waiter>();
        private readonly long capacity;
        private long used;

        public WeightedThrottle(long capacity)
        {
            this.capacity = Math.Max(1, capacity);
        }

        /// <summary>동시에 잡을 수 있는 자원의 총량</summary>
        public long Capacity { get { return capacity; } }

        public long Used { get { lock (locker) return used; } }

        public int WaitingCount { get { lock (locker) return waiters.Count; } }

        private sealed class Waiter : TaskCompletionSource<IDisposable>
        {
            public Waiter() : base(TaskCreationOptions.RunContinuationsAsynchronously) { }
            public long Units;
            public IDisposable CancelRegistration;
        }

        /// <summary>한 작업이 잡을 자원량을 유효 범위로 자른다. 혼자서는 항상 통과할 수 있다.</summary>
        public long Clamp(long units)
        {
            if (units < 1) units = 1;
            return Math.Min(units, capacity);
        }

        /// <summary>자원을 확보하면 해제용 리스를 돌려주는 Task 를 반환한다. FIFO 순서를 지킨다.</summary>
        public Task<IDisposable> AcquireAsync(long units)
        {
            units = Clamp(units);
            lock (locker)
            {
                if (waiters.Count == 0 && used + units <= capacity)
                {
                    used += units;
                    return Task.FromResult<IDisposable>(new Lease(this, units));
                }

                var waiter = new Waiter { Units = units };
                waiters.Enqueue(waiter);
                return waiter.Task;
            }
        }

        /// <summary>
        /// m1: 취소 가능한 대기. 화면에서 벗어난 썸네일 항목(ThumbViewerItem.Clear → Cancel)은
        /// 큐에서 빠져서 뒤에 보이는 항목을 막지 않는다. 이미 리스를 받은 뒤의 취소는
        /// 리스를 반납하고 호출자도 자신의 finally 에서 한 번 더 Dispose 해도 안전하다.
        /// </summary>
        public Task<IDisposable> AcquireAsync(long units, CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
                return Task.FromCanceled<IDisposable>(cancellationToken);
            units = Clamp(units);
            Waiter waiter;
            lock (locker)
            {
                if (waiters.Count == 0 && used + units <= capacity &&
                    !cancellationToken.IsCancellationRequested)
                {
                    used += units;
                    return Task.FromResult<IDisposable>(new Lease(this, units));
                }
                if (cancellationToken.IsCancellationRequested)
                    return Task.FromCanceled<IDisposable>(cancellationToken);
                waiter = new Waiter { Units = units };
                waiters.Enqueue(waiter);
            }
            waiter.CancelRegistration = cancellationToken.Register(() => CancelWaiter(waiter));
            waiter.Task.ContinueWith(_ =>
            {
                var r = waiter.CancelRegistration;
                if (r != null) r.Dispose();
            }, TaskContinuationOptions.ExecuteSynchronously);
            return waiter.Task;
        }

        private void CancelWaiter(Waiter waiter)
        {
            lock (locker)
            {
                if (waiters.Count > 0)
                {
                    var keep = new System.Collections.Generic.Queue<Waiter>(waiters.Count);
                    var found = false;
                    while (waiters.Count > 0)
                    {
                        var w = waiters.Dequeue();
                        if (w == waiter) found = true;
                        else keep.Enqueue(w);
                    }
                    while (keep.Count > 0) waiters.Enqueue(keep.Dequeue());
                    if (found)
                    {
                        waiter.TrySetCanceled();
                        return;
                    }
                }
            }
            // 이미 리스를 받은 뒤의 취소: 리스를 반납한다(호출자 finally 의 Dispose 와 중복돼도 안전).
            if (waiter.Task.Status == TaskStatus.RanToCompletion)
            {
                try { waiter.Task.Result.Dispose(); } catch { }
            }
        }

        private void Release(long units)
        {
            lock (locker)
            {
                used -= units;
                if (used < 0) used = 0;

                // 대기열 맨 앞부터 순서대로 통과시킨다(새치기 금지)
                while (waiters.Count > 0)
                {
                    var head = waiters.Peek();
                    if (head.Units > capacity - used) break;
                    waiters.Dequeue();
                    used += head.Units;
                    head.SetResult(new Lease(this, head.Units));
                }
            }
        }

        private sealed class Lease : IDisposable
        {
            private WeightedThrottle owner;
            private readonly long units;

            public Lease(WeightedThrottle owner, long units)
            {
                this.owner = owner;
                this.units = units;
            }

            public void Dispose()
            {
                var o = Interlocked.Exchange(ref owner, null);
                if (o != null) o.Release(units);
            }
        }
    }

    /// <summary>썸네일(원본 이미지) 디코딩 전용 제한기.</summary>
    internal static class ThumbnailLimiter
    {
        // 원본 1픽셀당 4바이트(32bpp) 정도를 쓴다고 보고 계산한다.
        public const int BytesPerPixel = 4;

        // 형식을 알 수 없는 항목(압축 파일/동영상 등)은 무거운 작업으로 본다.
        // 압축 파일 썬네일은 안의 이미지를 전부 읽어야 하므로 동시에 많이 돌리면 디스크가 
        // 포화된다. 이미지 확장자인데 헤더를 읽지 못한 경우는 어차피 곰방 실패하므로 가볍게 본다.
        private const long HeavyUnits = 256L * 1024 * 1024;
        private const long BrokenImageUnits = 16L * 1024 * 1024;

        public static readonly int MaxParallelism = GetMaxParallelism();

        private static readonly long Capacity = GetCapacity();

        private static readonly WeightedThrottle throttle = new WeightedThrottle(Capacity);

        public static long TotalCapacity { get { return Capacity; } }

        public static long UsedUnits { get { return throttle.Used; } }

        public static int WaitingCount { get { return throttle.WaitingCount; } }

        private static int GetMaxParallelism()
        {
            var cores = Environment.ProcessorCount;
            if (cores < 2) return 2;
            return Math.Min(cores, 16);
        }

        private static long GetCapacity()
        {
            long total = 0;
            try
            {
                total = (long)new Microsoft.VisualBasic.Devices.ComputerInfo().TotalPhysicalMemory;
            }
            catch
            {
                total = 0;
            }
            if (total <= 0) total = 4L * 1024 * 1024 * 1024; // 판단 불가 시 4GB 로 가정

            var capacity = total / 16; // 전체 메모리의 1/16 까지만 동시 디코딩에 쓴다
            const long lower = 384L * 1024 * 1024;
            const long upper = 1536L * 1024 * 1024;
            if (capacity < lower) capacity = lower;
            if (capacity > upper) capacity = upper;
            return capacity;
        }

        private static readonly string[] imageExtensionsInLowerWithoutPeriod =
            new string[] { "jpg", "jpeg", "png", "apng", "bmp", "gif", "ico", "tif", "tiff", "psd", "dds", "webp", "tga", "pdn", "jp2", "wdp", "jxr" };

        /// <summary>이 파일을 디코딩하는 데 필요할 것으로 보이는 메모리량(바이트) 을 추정한다.</summary>
        public static long EstimateUnits(string path)
        {
            var looksLikeImage = false;
            try
            {
                var extension = Path.GetExtension(path);
                looksLikeImage = extension != null &&
                    Array.IndexOf(imageExtensionsInLowerWithoutPeriod, extension.TrimStart('.').ToLower()) >= 0;
            }
            catch (Exception)
            {
                looksLikeImage = false;
            }

            long units;
            try
            {
                var info = new ImageInfo(path);
                var pixels = (long)info.Size.Width * info.Size.Height;
                units = pixels > 0 ? pixels * BytesPerPixel : BrokenImageUnits;
                var bpp = info.BitPerPixel;
                if (pixels > 0 && bpp > 32) units = pixels * (bpp / 8);
            }
            catch (Exception)
            {
                units = looksLikeImage ? BrokenImageUnits : HeavyUnits;
            }
            return units;
        }

        /// <summary>
        /// 최대 MaxParallelism 개까지만 동시에 실행되도록 하한을 두고, 큰 이미지는 메모리 예산까지 함께 본다.
        /// </summary>
        public static long WeighUnits(long estimatedUnits)
        {
            var floor = (Capacity + MaxParallelism - 1) / MaxParallelism;
            if (estimatedUnits < floor) estimatedUnits = floor;
            return estimatedUnits;
        }

        public static Task<IDisposable> AcquireAsync(string path)
        {
            return throttle.AcquireAsync(WeighUnits(EstimateUnits(path)));
        }

        /// <summary>m1: 취소 토큰 전달용. ThumbViewerItem.Clear() 의 Cancel 이 큐 대기까지 취소한다.</summary>
        public static Task<IDisposable> AcquireAsync(string path, CancellationToken cancellationToken)
        {
            return throttle.AcquireAsync(WeighUnits(EstimateUnits(path)), cancellationToken);
        }

        /// <summary>측정/검사용</summary>
        public static Task<IDisposable> AcquireAsync(long estimatedUnits)
        {
            return throttle.AcquireAsync(WeighUnits(estimatedUnits));
        }

        /// <summary>m1: 측정/검사용 취소 토큰 전달</summary>
        public static Task<IDisposable> AcquireAsync(long estimatedUnits, CancellationToken cancellationToken)
        {
            return throttle.AcquireAsync(WeighUnits(estimatedUnits), cancellationToken);
        }
    }
}
