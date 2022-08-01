using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

using Discord.WebSocket;

using Dalamud.Logging;
using Dalamud.Utility;
using Discord;
using JetBrains.Annotations;

namespace Dalamud.DiscordBridge
{
    public class LogHelper
    {
        public LogHelper(string tag, Action<string> log)
        {
            _tag = tag;
            _log = log;
        }

        private static string FormatValue(object value)
        {
            return value is string ? $"\"{value}\"" : value.ToString();
        }

        public void LogExpr(object value, [CallerArgumentExpression("value")] string name = null)
        {
            Log($"{name}: {FormatValue(value)}");
        }

        public void LogValue(object value)
        {
            Log($"{FormatValue(value)}");
        }
        
        public void Log(string message, bool useBullet = true)
        {
            var effectiveLevel = useBullet ? Math.Max(_level - 1, 0) : _level;
            var bulletStr = useBullet ? _bullet : "";
            
            var tagBlock = _tag.IsNullOrEmpty() ? "" : $"[{_tag}] "; 
            var prefix = tagBlock + string.Concat(Enumerable.Repeat(_tab, effectiveLevel)) + bulletStr;

            var lines = message.Split(
                new [] { "\r\n", "\r", "\n" },
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            foreach (var line in lines)
            {
                _log(prefix + line);
            }
        }

        public void Push()
        {
            if (_level < _maxLevel)
            {
                ++_level;
            }
        }

        public void Push(string message)
        {
            Log(message, useBullet: false);

            Push();
        }

        public void Pop()
        {
            if (_level > 0)
            {
                --_level;
            }
        }

        public void Pop(string message)
        {
            Log(message);

            Pop();
        }

        private const string _tab = "  ";
        private const string _bullet = "- ";
        private const int _maxLevel = 10;
        
        private int _level = 0;
        private readonly Action<string> _log;
        private readonly string _tag;
    }
    
    public class DuplicateFilter
    {
        public DuplicateFilter()
        {
            LogHelper.Log("START ===========================================================================");
        }
        
        #region Methods
        
        public void Add(SocketMessage message)
        {
            if (_recentMessages.All(m => m.Id != message.Id) &&
                message.Author.IsWebhook &&
                !message.Content.IsNullOrEmpty())
            {
                _recentMessages.Add(message);
            }
        }
        
        public bool IsRecentlySent(string displayName, string chatText)
        {
            //todo this is wrong because msg.Content needs to be parsed to get the chatText
            // check for duplicates before sending
            // straight up copied from the previous bot, but I have no way to test this myself.
            var recentMsg = _recentMessages.FirstOrDefault(msg =>
                IsDuplicate(msg.Author.Username, msg.Content, displayName, chatText));

            //if (this.plugin.Config.DuplicateCheckMS > 0 && recentMsg != null)
            if (recentMsg != null)
            {
                long msgDiff = GetElapsedMs(recentMsg);
                LogHelper.Log($"{nameof(msgDiff)}: {msgDiff}");
                
                //if (msgDiff < this.plugin.Config.DuplicateCheckMS)
                if (msgDiff < OutgoingFilterIntervalMs)
                {
                    LogHelper.Log("(FILTERED)");
                    
                    return true;
                }
            }
            
            return false;
        }

        public async Task Dedupe()
        {
            var filteredMessages = _recentMessages.Where(m => GetElapsedMs(m) < RecentIntervalMs).ToArray();

            var deletedMessages = new List<SocketMessage>();
            
            //todo: check if there's a cleaner/linq way to compare every item to every other item 
            for (var i = 0; i < filteredMessages.Length; i++)
            {
                var recent = filteredMessages[i];

                for (var j = i + 1; j < filteredMessages.Length; j++)
                {
                    var other = filteredMessages[j];

                    if (IsDuplicate(recent, other))
                    {
                        SocketMessage deletedMessage = await DeleteMostRecent(recent, other);

                        deletedMessages.Add(deletedMessage);
                    }
                }
            }

            _recentMessages = new List<SocketMessage>(filteredMessages.Except(deletedMessages));
        }

        #endregion
        
        #region Private Functions

        private static long GetElapsedMs(DateTimeOffset timestamp)
        {
            return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - timestamp.ToUnixTimeMilliseconds();
        }

        private static long GetElapsedMs(SocketMessage message)
        {
            return GetElapsedMs(message.Timestamp);
        }

        private async Task<SocketMessage> DeleteMostRecent(SocketMessage left, SocketMessage right)
        {
            bool leftIsNewer = GetElapsedMs(left.CreatedAt) > GetElapsedMs(right.CreatedAt);
            
            SocketMessage target = leftIsNewer ? left : right;
            
            _ = await TryDeleteAsync(target);
            
            return target;
        }

        private static async Task<bool> TryDeleteAsync(SocketMessage message)
        {
            try
            {
                await message.DeleteAsync();
                
                return true;
            }
            catch (Discord.Net.HttpException ex)
            {
                // 404 Not Found is expected if the message was already deleted.
                // Otherwise, it's an unexpected error so rethrow.
                if (ex.HttpCode != HttpStatusCode.NotFound)
                {
                    throw;
                }
            }
            
            return false;
        }
        
        private static bool IsDuplicate(SocketMessage left, SocketMessage right)
        {
            return IsDuplicate(left.Author.Username, left.Content, right.Author.Username, right.Content);
        }

        private static bool IsDuplicate(string leftDisplayName, string leftContent, string rightDisplayName, string rightContent)
        {
            string leftChatText = GetChatText(leftContent);
            string rightChatText = GetChatText(rightContent);

            return leftDisplayName == rightDisplayName &&
                   leftChatText == rightChatText;
        }

        private static string GetChatText(string recentContent)
        {
            var matches = ExtractChatText.Match(recentContent);

            return matches.Groups[GroupChatText].Value;
        }
        
        #endregion
        
        #region Private Data

        private static readonly LogHelper LogHelper = new LogHelper("FILTER", m => { PluginLog.LogWarning(m);});

        private const long OutgoingFilterIntervalMs = 2000;
        private const long RecentIntervalMs = 10000;
        
        private const string GroupPrefix = "prefix"; 
        private const string GroupSlug = "slug"; 
        private const string GroupChatText = "chatText"; 
        private static readonly Regex ExtractChatText = new Regex(@$"(?'{GroupPrefix}'.*)\*\*\[(?'{GroupSlug}'.+)\]\*\* (?'{GroupChatText}'.+)");

        private List<SocketMessage> _recentMessages = new();

        #endregion
    }
}