using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace MixerOnline.Core.Network
{
    public class BaseMessage
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty;
    }

    public class HelloMessage : BaseMessage
    {
        [JsonPropertyName("client")]
        public string Client { get; set; } = string.Empty;

        [JsonPropertyName("version")]
        public string Version { get; set; } = string.Empty;
    }

    public class TabItem
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("title")]
        public string Title { get; set; } = string.Empty;

        [JsonPropertyName("url")]
        public string Url { get; set; } = string.Empty;

        [JsonPropertyName("audible")]
        public bool Audible { get; set; }

        [JsonPropertyName("favIconUrl")]
        public string? FavIconUrl { get; set; }

        public override string ToString()
        {
            return Audible ? $"🔊 {Title}" : Title;
        }
    }

    public class TabListMessage : BaseMessage
    {
        [JsonPropertyName("tabs")]
        public List<TabItem> Tabs { get; set; } = new();
    }

    public class SelectTabMessage : BaseMessage
    {
        [JsonPropertyName("tabId")]
        public int TabId { get; set; }
    }

    public class CaptureStatusMessage : BaseMessage
    {
        [JsonPropertyName("tabId")]
        public int TabId { get; set; }

        [JsonPropertyName("title")]
        public string Title { get; set; } = string.Empty;

        [JsonPropertyName("sampleRate")]
        public int SampleRate { get; set; } = 48000;

        [JsonPropertyName("channels")]
        public int Channels { get; set; } = 2;

        [JsonPropertyName("active")]
        public bool Active { get; set; }
    }

    public class TabNotificationMessage : BaseMessage
    {
        [JsonPropertyName("tabId")]
        public int TabId { get; set; }

        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("url")]
        public string? Url { get; set; }
    }

    public class ErrorNotificationMessage : BaseMessage
    {
        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;
    }
}
