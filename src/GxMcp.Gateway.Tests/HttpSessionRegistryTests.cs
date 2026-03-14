using System;
using System.Linq;
using System.Threading;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public class HttpSessionRegistryTests
    {
        [Fact]
        public void Create_ShouldRegisterSession()
        {
            var registry = new HttpSessionRegistry(TimeSpan.FromMinutes(5));

            var session = registry.Create();

            Assert.False(string.IsNullOrWhiteSpace(session.Id));
            Assert.Single(registry.ActiveSessions);
        }

        [Fact]
        public void TryGet_ShouldReturnFalseForExpiredSession()
        {
            var registry = new HttpSessionRegistry(TimeSpan.FromMilliseconds(1));
            var session = registry.Create();

            System.Threading.Thread.Sleep(20);

            var found = registry.TryGet(session.Id, out var resolved);

            Assert.False(found);
            Assert.Null(resolved);
            Assert.Empty(registry.ActiveSessions);
        }

        [Fact]
        public void Enqueue_ShouldTrimOldMessagesWhenQueueLimitIsReached()
        {
            var registry = new HttpSessionRegistry(TimeSpan.FromMinutes(5), maxQueuedMessagesPerSession: 2);
            var session = registry.Create();

            registry.Enqueue(session, "one");
            registry.Enqueue(session, "two");
            registry.Enqueue(session, "three");

            var messages = session.PendingMessages.ToArray();

            Assert.Equal(2, messages.Length);
            Assert.Equal(new[] { "two", "three" }, messages);
        }

        [Fact]
        public void Remove_ShouldDeleteSession()
        {
            var registry = new HttpSessionRegistry(TimeSpan.FromMinutes(5));
            var session = registry.Create();

            var removed = registry.Remove(session.Id);

            Assert.True(removed);
            Assert.Empty(registry.ActiveSessions);
        }

        [Fact]
        public void CleanupExpired_ShouldRemoveExpiredSessions()
        {
            var registry = new HttpSessionRegistry(TimeSpan.FromMilliseconds(5));
            var session = registry.Create();

            Thread.Sleep(20);

            var removed = registry.CleanupExpired();

            Assert.Equal(1, removed);
            Assert.False(registry.TryGet(session.Id, out _));
        }

        [Fact]
        public void TryGet_ShouldRefreshLastSeenForActiveSession()
        {
            var registry = new HttpSessionRegistry(TimeSpan.FromMinutes(5));
            var session = registry.Create();
            var previousLastSeen = session.LastSeenUtc;

            Thread.Sleep(20);

            var found = registry.TryGet(session.Id, out var resolved);

            Assert.True(found);
            Assert.NotNull(resolved);
            Assert.True(resolved!.LastSeenUtc > previousLastSeen);
        }
    }
}
