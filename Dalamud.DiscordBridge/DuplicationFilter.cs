using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

using Discord.WebSocket;

using Dalamud.Logging;
using Dalamud.Utility;

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
        public const bool Enabled = true;
        // public DuplicateFilter()
        // {
        // }
        
        #region Methods

        private List<SocketMessage> recentMessages = new();
        
        public void Add(SocketMessage message)
        {
            if (!Enabled) return;
            
            _logHelper.Push("ADD");
            _logHelper.LogExpr(message.Author.Username);
            _logHelper.LogExpr(message.Content);

            if (recentMessages.All(m => m.Id != message.Id) &&
                message.Author.IsWebhook &&
                !message.Content.IsNullOrEmpty())
            {
                recentMessages.Add(message);
                
                _logHelper.Log("(ADDED)");
            }
            else
            {
                _logHelper.Log("(SKIPPED)");
            }
            
            _logHelper.Pop();
        }
        
        public bool IsRecentlySent(string displayName, string chatText)
        {
            if (!Enabled) return false;
            
            _logHelper.Push("SEND");
            _logHelper.LogExpr(displayName);
            _logHelper.LogExpr(chatText);

            // check for duplicates before sending
            // straight up copied from the previous bot, but I have no way to test this myself.
            var recentMsg = recentMessages.FirstOrDefault(msg =>
                IsDuplicate(msg.Author.Username, msg.Content, displayName, chatText));

            //if (this.plugin.Config.DuplicateCheckMS > 0 && recentMsg != null)
            if (recentMsg != null)
            {
                long msgDiff = GetElapsedMs(recentMsg);
                _logHelper.Log($"{nameof(msgDiff)}: {msgDiff}");
                
                //if (msgDiff < this.plugin.Config.DuplicateCheckMS)
                if (msgDiff < OutgoingFilterIntervalMs)
                {
                    _logHelper.Pop("(FILTERED)");
                    
                    return true;
                }
            }
            
            _logHelper.Pop("(ALLOWED)");
            
            return false;

            // the message to a list of recently sent messages. 
            // If someone else sent the same thing at the same time
            // both will need to be checked and the earlier timestamp kept
            // while the newer one is removed
            // refer to https://discord.com/channels/581875019861328007/684745859497590843/791207648619266060
        }

        public async Task Dedupe()
        {
            if (!Enabled) return;
            
            _logHelper.Push("DEDUPE");

            var dt = GetElapsedMs(lastUpdate);
            _logHelper.Log($"{dt}ms since last dedupe");
            if (dt < DedupeIntervalMs)
            {
                _logHelper.Pop($"SKIPPED");
                
                return;
            }
            
            var recentMessages = this.recentMessages.Where(m => GetElapsedMs(m) < RecentIntervalMs);
            var socketMessages = recentMessages as SocketMessage[] ?? recentMessages.ToArray();
            var content = socketMessages.Select(m => m.Content);

            //todo: all of this needs to be made more efficient in terms of linq and collection operations
            //todo: - use a set?
            
            _logHelper.Push("Recents:");
            _logHelper.Log($"Total: {this.recentMessages.Count()}");
            if (this.recentMessages.Count() == 0)
            {
                _logHelper.Pop();
                _logHelper.Pop();
                return;
            }
            _logHelper.Log($"Recent: {socketMessages.Count()}");
            _logHelper.Pop();

            var deletedMessages = new List<SocketMessage>();
            
            if (socketMessages.Count() > 0)
            {
                _logHelper.Push("Content:");
                
                foreach (var chatText in content)
                {
                    _logHelper.LogValue(chatText);
                }
                
                _logHelper.Pop();
                

                //todo: check if there's a cleaner/linq way to compare every item to every other item 
                for (var i = 0; i < socketMessages.Length; i++)
                {
                    var recent = socketMessages[i];

                    for (var j = i + 1; j < socketMessages.Length; j++)
                    {
                        var other = socketMessages[j];

                        if (IsDuplicate(recent, other))
                        {
                            bool wasDeleted = await DeleteMostRecent(recent, other);

                            if (wasDeleted)
                            {
                                deletedMessages.Add(recent);
                                deletedMessages.Add(other);
                            }
                        }
                    }
                }
            }

            this.recentMessages = new List<SocketMessage>(recentMessages.Except(deletedMessages));
            _logHelper.Log($"Deleted Count: {deletedMessages.Count()}");
            _logHelper.Log($"Final Total: {this.recentMessages.Count()}");
            
            _logHelper.Pop();
        }

        #endregion
        
        #region Private Functions
        
        private static void LogDedupe(string message)
        {
            _logHelper.Log(message);
            //PluginLog.LogDebug($"[DEDUPE] {message}");
        }

        private static readonly LogHelper _logHelper = new LogHelper("FILTER", m => { PluginLog.LogWarning(m);});

        private static long GetElapsedMs(DateTimeOffset timestamp)
        {
            return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - timestamp.ToUnixTimeMilliseconds();
        }

        private static long GetElapsedMs(SocketMessage message)
        {
            return GetElapsedMs(message.Timestamp);
        }

        private static long DifferenceMs(DateTimeOffset left, DateTimeOffset right)
        {
            return GetElapsedMs(left) - GetElapsedMs(right);
        }

        private async Task<bool> DeleteMostRecent(SocketMessage recent, SocketMessage other)
        {
            if (recent.Timestamp > other.Timestamp)
            {
                return await TryDeleteAsync(recent);
            }
            else
            {
                return await TryDeleteAsync(other);
            }
        }

        private static async Task<bool> TryDeleteAsync(SocketMessage message)
        {
            _logHelper.Push("DELETE MESSAGE");
            _logHelper.LogExpr(message.Author.Username);
            _logHelper.LogExpr(message.Content);

            if (!message.Author.IsWebhook)
            {
                _logHelper.Log("NOT WEBHOOK: Should only delete webhook messages");
            }

            try
            {
                await message.DeleteAsync();
                
                _logHelper.Pop("(SUCCESS)");
                
                return true;
            }
            catch (Discord.Net.HttpException)
            {
                _logHelper.Log($"(MESSAGE NOT FOUND)");
            }
            
            _logHelper.Pop();
            
            return false;
        }
        
        private bool IsDuplicate(SocketMessage recent, SocketMessage other)
        {
            _logHelper.Push("COMPARE");

            bool sameUser = recent.Author.Username == other.Author.Username;
            //sameUser = true;
            //_logHelper.Log($"sameUser: {recent.Author.Username} //// {other.Author.Username}, {sameUser}");

            //bool withinTime = Math.Abs(DifferenceMs(recent.Timestamp, other.Timestamp)) < ComparisonIntervalMs;
            //_logHelper.Log($"withinTime: {Math.Abs(DifferenceMs(recent.Timestamp, other.Timestamp))}ms");

            bool differentId = recent.Id != other.Id;

            string leftText = GetText(recent.Content);
            string rightText = GetText(other.Content);
            _logHelper.LogExpr(leftText);
            _logHelper.LogExpr(rightText);

            var result = sameUser && differentId && (leftText == rightText);
            _logHelper.Pop($"(RETURN {result})");
            return result;
        }

        private bool IsDuplicate(string leftDisplayName, string leftContent, string rightDisplayName, string rightContent)
        {
            _logHelper.Push("COMPARE (SIMPLE)");
            
            string leftChatText = GetText(leftContent);
            string rightChatText = GetText(rightContent);

            bool result = leftDisplayName == rightDisplayName &&
                   leftChatText == rightChatText;

            _logHelper.Pop($"(RETURN {result})");
            
            return result;
        }

        private static string GetText(string recentContent)
        {
            var matches = ExtractChatText.Match(recentContent);

            return matches.Groups[GroupChatText].Value;
        }
        
        #endregion
        
        #region Private Data

        private const long OutgoingFilterIntervalMs = 2000;
        private const long DedupeIntervalMs = 1000;
        private const long RecentIntervalMs = 10000;
        private const long ComparisonIntervalMs = 3000;
        
        private const string GroupPrefix = "prefix"; 
        private const string GroupSlug = "slug"; 
        private const string GroupChatText = "chatText"; 
        private static readonly Regex ExtractChatText = new Regex(@$"(?'{GroupPrefix}'.*)\*\*\[(?'{GroupSlug}'.+)\]\*\* (?'{GroupChatText}'.+)");
        // (?'GroupPrefix'.*)\*?\*?\[(?'GroupSlug'.+)\]\*?\*? (?'GroupChatText'.+)

        private DateTimeOffset lastUpdate = DateTimeOffset.Now;
        
        #endregion
    }
}