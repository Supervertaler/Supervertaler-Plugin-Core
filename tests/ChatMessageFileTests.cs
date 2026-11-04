using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization.Json;
using System.Text;
using Supervertaler.Core.Models;

namespace Supervertaler.Core.Tests
{
    /// <summary>
    /// ChatMessage as the Trados AI Assistant saves it: chat_history.json and its
    /// archives are a List&lt;ChatMessage&gt; written with DataContractJsonSerializer.
    /// <see cref="ChatMessage.AskedWhile"/> (205) must survive a save, must not
    /// appear in a message that has none (so every other message, and memoQ's,
    /// is written as before), and a file saved before it existed must still load.
    /// </summary>
    [Tests]
    internal static class ChatMessageFileTests
    {
        private static string Save(List<ChatMessage> messages)
        {
            var serializer = new DataContractJsonSerializer(typeof(List<ChatMessage>));
            using (var ms = new MemoryStream())
            {
                serializer.WriteObject(ms, messages);
                return Encoding.UTF8.GetString(ms.ToArray());
            }
        }

        private static List<ChatMessage> Load(string json)
        {
            var serializer = new DataContractJsonSerializer(typeof(List<ChatMessage>));
            using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                return (List<ChatMessage>)serializer.ReadObject(ms);
        }

        public static void AskedWhile_SurvivesASaveAndLoad()
        {
            var saved = new List<ChatMessage>
            {
                new ChatMessage { Role = ChatRole.User, Content = "Is this segment right?", AskedWhile = "segment 4 of 54" },
                new ChatMessage { Role = ChatRole.Assistant, Content = "Yes." }
            };

            var loaded = Load(Save(saved));

            Assert.Equal(2, loaded.Count, "both messages back");
            Assert.Equal("segment 4 of 54", loaded[0].AskedWhile, "the segment the question was asked on");
            Assert.Equal("Is this segment right?", loaded[0].Content, "the text as typed, without the segment");
            Assert.Equal(null, loaded[0].DisplayContent, "nothing for the chat to show instead");
            Assert.Equal(null, loaded[1].AskedWhile, "an answer has none");
        }

        public static void AMessageWithoutIt_IsSavedAsBefore()
        {
            var json = Save(new List<ChatMessage>
            {
                new ChatMessage { Role = ChatRole.User, Content = "Q", Timestamp = new DateTime(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc) }
            });

            Assert.True(!json.Contains("askedWhile"), "no new field in the file: " + json);
            Assert.Equal("[{\"content\":\"Q\",\"role\":0,\"timestamp\":\"\\/Date(1793793600000)\\/\"}]", json,
                "the same JSON as a message saved before 205");
        }

        public static void AFileSavedBeforeIt_StillLoads()
        {
            const string before205 =
                "[{\"content\":\"Old question\",\"displayContent\":\"Old\",\"role\":0,\"timestamp\":\"\\/Date(1762000000000)\\/\"}," +
                "{\"content\":\"Old answer\",\"role\":1,\"timestamp\":\"\\/Date(1762000001000)\\/\"}]";

            var loaded = Load(before205);

            Assert.Equal(2, loaded.Count, "both messages");
            Assert.Equal(null, loaded[0].AskedWhile, "no segment recorded");
            Assert.Equal("Old question", loaded[0].Content, "the question");
            Assert.Equal("Old", loaded[0].DisplayContent, "its display text");
            Assert.Equal(ChatRole.Assistant, loaded[1].Role, "the answer");
        }
    }
}
