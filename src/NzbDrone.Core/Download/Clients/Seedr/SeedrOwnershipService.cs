using System;
using System.Collections.Concurrent;
using NLog;
using NzbDrone.Common.Extensions;
using StackExchange.Redis;

namespace NzbDrone.Core.Download.Clients.Seedr
{
    public interface ISeedrOwnershipService
    {
        void ClaimOwnership(string infoHash, SeedrSettings settings);
        bool? ReleaseOwnership(string infoHash, SeedrSettings settings);
        bool? IsOwnedByMe(string infoHash, SeedrSettings settings);
        string TestConnection(SeedrSettings settings);
    }

    public class SeedrRedisOwnershipService : ISeedrOwnershipService
    {
        private static readonly ConcurrentDictionary<string, Lazy<ConnectionMultiplexer>> Connections = new ();
        private static readonly TimeSpan OwnershipTtl = TimeSpan.FromDays(7);
        private readonly Logger _logger;

        // Lua script: atomically remove member, check remaining count, delete key if empty or refresh TTL
        private const string ReleaseScript = @"
redis.call('SREM', KEYS[1], ARGV[1])
local remaining = redis.call('SCARD', KEYS[1])
if remaining == 0 then
    redis.call('DEL', KEYS[1])
    return 1
else
    redis.call('EXPIRE', KEYS[1], ARGV[2])
    return 0
end";

        public SeedrRedisOwnershipService(Logger logger)
        {
            _logger = logger;
        }

        public void ClaimOwnership(string infoHash, SeedrSettings settings)
        {
            if (!settings.SharedAccount || settings.InstanceTag.IsNullOrWhiteSpace() || settings.RedisConnectionString.IsNullOrWhiteSpace())
            {
                return;
            }

            try
            {
                var db = GetDatabase(settings.RedisConnectionString);
                var key = GetKey(infoHash);
                db.SetAdd(key, settings.InstanceTag);
                db.KeyExpire(key, OwnershipTtl);
                _logger.Debug("Claimed ownership of {0} for instance '{1}'", infoHash, settings.InstanceTag);
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Failed to claim Redis ownership for {0}", infoHash);
            }
        }

        public bool? ReleaseOwnership(string infoHash, SeedrSettings settings)
        {
            if (!settings.SharedAccount || settings.InstanceTag.IsNullOrWhiteSpace() || settings.RedisConnectionString.IsNullOrWhiteSpace())
            {
                return null;
            }

            try
            {
                var db = GetDatabase(settings.RedisConnectionString);
                var key = GetKey(infoHash);
                var ttlSeconds = (int)OwnershipTtl.TotalSeconds;

                var result = (int)db.ScriptEvaluate(ReleaseScript, new RedisKey[] { key }, new RedisValue[] { settings.InstanceTag, ttlSeconds });

                var isLast = result == 1;
                _logger.Debug("Released ownership of {0} for instance '{1}'. Last owner: {2}", infoHash, settings.InstanceTag, isLast);
                return isLast;
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Failed to release Redis ownership for {0}. Erring on safety — skipping cloud deletion.", infoHash);
                return null;
            }
        }

        public bool? IsOwnedByMe(string infoHash, SeedrSettings settings)
        {
            if (!settings.SharedAccount || settings.InstanceTag.IsNullOrWhiteSpace() || settings.RedisConnectionString.IsNullOrWhiteSpace())
            {
                return null;
            }

            try
            {
                var db = GetDatabase(settings.RedisConnectionString);
                var key = GetKey(infoHash);
                return db.SetContains(key, settings.InstanceTag);
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Failed to check Redis ownership for {0}", infoHash);
                return null;
            }
        }

        public string TestConnection(SeedrSettings settings)
        {
            if (settings.RedisConnectionString.IsNullOrWhiteSpace())
            {
                return null;
            }

            try
            {
                var db = GetDatabase(settings.RedisConnectionString);
                var pong = db.Ping();
                _logger.Debug("Redis PING responded in {0}ms", pong.TotalMilliseconds);
                return null;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        private static string GetKey(string infoHash)
        {
            return $"seedr:owners:{infoHash}";
        }

        private static IDatabase GetDatabase(string connectionString)
        {
            var multiplexer = Connections.GetOrAdd(connectionString, cs => new Lazy<ConnectionMultiplexer>(() =>
            {
                var options = ConfigurationOptions.Parse(cs);
                options.AbortOnConnectFail = false;
                options.ConnectTimeout = 5000;
                options.SyncTimeout = 3000;
                return ConnectionMultiplexer.Connect(options);
            }));

            return multiplexer.Value.GetDatabase();
        }
    }
}
