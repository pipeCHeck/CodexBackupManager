using System;
using System.IO;
using System.Threading;
using CodexBackupManager.Backup.Container;
using Xunit;

namespace CodexBackupManager.Backup.Tests.Container;

/// <summary>
/// Phase 08_01 — <see cref="StreamingHashCopy.CopyWithHash"/>가 실제로 진행 중인 스트리밍 복사
/// 도중 취소되면 정확히 <see cref="OperationCanceledException"/>을 던지는지 확인한다. GitHub Actions
/// CI에서 "300MB 파일 + 30ms 뒤 취소"라는 wall-clock race 테스트가 timing에 따라 실패한 문제를
/// 고치기 위해, 커스텀 Stream으로 "N번째 Read 직후 취소"를 완전히 결정적으로 재현한다 — 실제 파일
/// 크기나 실행 시간에 전혀 의존하지 않는다.
/// </summary>
public sealed class StreamingHashCopyTests
{
    /// <summary>
    /// <paramref name="cancelAfterReadCount"/>번째 <see cref="Read"/> 호출까지는 정상적으로 더미
    /// 바이트를 돌려주고, 그다음 호출부터는(반환하기 직전) <paramref name="cts"/>를 취소한다 —
    /// <see cref="StreamingHashCopy.CopyWithHash"/>의 루프가 그 직후 <c>ThrowIfCancellationRequested</c>
    /// 에서 예외를 던지게 만든다. 실제 파일이 아니라 순수 메모리 기반이라 크기/속도에 좌우되지 않는다.
    /// </summary>
    private sealed class CancelAfterNReadsStream(CancellationTokenSource cts, int cancelAfterReadCount, long totalLength) : Stream
    {
        private long _position;
        private int _readCount;

        public int ReadCallCount => _readCount;

        public override int Read(byte[] buffer, int offset, int count)
        {
            _readCount++;
            if (_readCount > cancelAfterReadCount)
            {
                cts.Cancel();
            }

            long remaining = totalLength - _position;
            if (remaining <= 0)
            {
                return 0;
            }

            int toRead = (int)Math.Min(count, remaining);
            Array.Fill(buffer, (byte)'a', offset, toRead);
            _position += toRead;
            return toRead;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => totalLength;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public void 지정한_Read_횟수_이후_취소되면_정확히_OperationCanceledException을_던진다()
    {
        using var cts = new CancellationTokenSource();
        var source = new CancelAfterNReadsStream(cts, cancelAfterReadCount: 3, totalLength: 50L * 1024 * 1024);
        using var destination = new MemoryStream();

        Assert.Throws<OperationCanceledException>(() => StreamingHashCopy.CopyWithHash(source, destination, cts.Token));

        // 취소 직후에 멈췄다는 것도 함께 확인한다 — 남은 수십 MB를 계속 읽어 내려가지 않았다.
        Assert.Equal(4, source.ReadCallCount);
    }

    [Fact]
    public void 취소되지_않으면_전체를_스트리밍으로_복사하고_정확한_해시를_돌려준다()
    {
        byte[] expected = new byte[5 * 1024 * 1024];
        Array.Fill(expected, (byte)'a');
        using var source = new MemoryStream(expected);
        using var destination = new MemoryStream();

        StreamingHashCopy.Result result = StreamingHashCopy.CopyWithHash(source, destination);

        Assert.Equal(expected.LongLength, result.ByteLength);
        Assert.Equal(expected, destination.ToArray());
    }
}
