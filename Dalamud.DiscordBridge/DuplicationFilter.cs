using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Dalamud.DiscordBridge.Model;
using Dalamud.DiscordBridge.XivApi;
using Dalamud.Game.Text;
using Dalamud.Logging;
using Dalamud.Utility;
using Discord;
using Discord.Net.Providers.WS4Net;
using Discord.Webhook;
using Discord.WebSocket;
using Lumina.Text;
using NetStone;
using NetStone.Model.Parseables.Character;
using NetStone.Model.Parseables.Search.Character;
using NetStone.Search.Character;

namespace Dalamud.DiscordBridge
{
    public class DuplicateFilter
    {
        
        public static DateTimeOffset StartTime = DateTimeOffset.Now;
        public static Stopwatch DedupeTimer = new Stopwatch();

        private static void LogDedupe(string message)
        {
            PluginLog.LogDebug($"[DEDUPE] {message}");
        }

        public bool IsRecentlySent(string displayName, string chatText, SocketChannel socketChannel)
        {
            // check for duplicates before sending
            // straight up copied from the previous bot, but I have no way to test this myself.
            var cachedMessages = (socketChannel as SocketTextChannel).GetCachedMessages();
            var recentMsg = cachedMessages.FirstOrDefault(msg =>
                IsDuplicate(msg.Author.Username, msg.Content, displayName, chatText));

                
            //if (this.plugin.Config.DuplicateCheckMS > 0 && recentMsg != null)
            if (recentMsg != null)
            {
                long msgDiff = GetElapsedMs(recentMsg);
                    
                //if (msgDiff < this.plugin.Config.DuplicateCheckMS)
                if (msgDiff < 2000)
                {
                    LogDedupe($"DIFF:{msgDiff}ms Skipping duplicate message: {chatText}");
                    return true;
                }
                        
            }
                
            LogDedupe($"Sending: {displayName}, {chatText}");
            return false;

            // the message to a list of recently sent messages. 
            // If someone else sent the same thing at the same time
            // both will need to be checked and the earlier timestamp kept
            // while the newer one is removed
            // refer to https://discord.com/channels/581875019861328007/684745859497590843/791207648619266060
        }

        public async Task Dedupe(SocketChannel socketChannel)
        {
            //GetElapsedMs(StartTime);
            if (DedupeTimer.ElapsedMilliseconds < 1000)
            {
                if (!DedupeTimer.IsRunning) DedupeTimer.Start();
                //LogDedupe("No-op");
                return;
            }
            DedupeTimer.Restart();
            
            if (socketChannel is not SocketTextChannel socketTextChannel)
            {
                return;
            }

            var cachedMessages = socketTextChannel.GetCachedMessages();

            var recentMessages = cachedMessages.Where(m => GetElapsedMs(m) < 10000);
            var socketMessages = recentMessages as SocketMessage[] ?? recentMessages.ToArray();
            var content = socketMessages.Select(m => m.Content);

            LogDedupe("Dedupe cached messages");
            LogDedupe($"- Total: {cachedMessages.Count()}");
            LogDedupe($"- Recent: {socketMessages.Count()}");
            LogDedupe($"- Content: {string.Join(", ", content)}");

            for (var i = 0; i < socketMessages.Length; i++)
            {
                var recent = socketMessages[i];

                for (var j = 0; j < socketMessages.Length; j++)
                {
                    if (i != j)
                    {
                        var other = socketMessages[j];

                        if (IsDuplicate(recent, other))
                        {
                            await DeleteMostRecent(recent, other);
                        }
                    }
                }
            }
        }

        private static long GetElapsedMs(DateTimeOffset timestamp)
        {
            return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - timestamp.ToUnixTimeMilliseconds();
        }

        private static long GetElapsedMs(SocketMessage message)
        {
            return GetElapsedMs(message.Timestamp);
            //return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - message.Timestamp.ToUnixTimeMilliseconds();
        }

        private static long DifferenceMs(DateTimeOffset left, DateTimeOffset right)
        {
            return GetElapsedMs(left) - GetElapsedMs(right);
        }

        private async Task DeleteMostRecent(SocketMessage recent, SocketMessage other)
        {
            if (recent.Timestamp > other.Timestamp)
            {
                await TryDeleteAsync(recent);
            }
            else
            {
                await TryDeleteAsync(other);
            }
        }

        private static async Task TryDeleteAsync(SocketMessage message)
        {
            LogDedupe($"Delete: ({message.Author.Username}) {message.Content}");

            // if (message.Author.IsBot)
            // {
               //  PluginLog.LogInformation($"AUTHOR IS BOT");
               //  return;
            // }

            try
            {
                await message.DeleteAsync();
            }
            catch (Discord.Net.HttpException)
            {
                LogDedupe($"Message could not be deleted");
            }
        }


        private const string GroupPrefix = "prefix"; 
        private const string GroupSlug = "slug"; 
        private const string GroupText = "text"; 
        private static readonly Regex ExtractChatText = new Regex(@$"(?'{GroupPrefix}'.*)(?'{GroupSlug}'\[.+\]) (?'{GroupText}'.+)");
        
        private bool IsDuplicate(SocketMessage recent, SocketMessage other)
        {
            string left = recent.Content;
            string right = other.Content;

            bool notEmptyString = !(recent.Content.IsNullOrEmpty() && other.Content.IsNullOrEmpty());

            bool bothWebhook = recent.Author.IsWebhook && other.Author.IsWebhook;
            bothWebhook = true;

            bool sameUser = recent.Author.Username == other.Author.Username;
            sameUser = true;
            LogDedupe($"USERS: {recent.Author.Username} //// {other.Author.Username}, {sameUser}");

            bool withinTime = Math.Abs(DifferenceMs(recent.Timestamp, other.Timestamp)) < 3000;
            LogDedupe($"withinTime: {Math.Abs(DifferenceMs(recent.Timestamp, other.Timestamp))}ms");

            bool differentId = recent.Id != other.Id;

            string leftText = GetText(recent.Content);
            string rightText = GetText(other.Content);

            return notEmptyString && bothWebhook && sameUser && withinTime && differentId && (leftText == rightText);
        }

        private bool IsDuplicate(string leftDisplayName, string leftContent, string rightDisplayName, string rightContent)
        {
            string leftChatText = GetText(leftContent);
            string rightChatText = GetText(rightContent);

            return leftDisplayName == rightDisplayName &&
                   leftChatText == rightChatText;
        }

        private static string GetText(string recentContent)
        {
            var matches = ExtractChatText.Match(recentContent);

            return matches.Groups[GroupText].Value;
        }
    }
}