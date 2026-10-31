using System;
using DotNetEnv;
using Anthropic;
using Anthropic.Models.Messages;

namespace ClaudeReminder
{

    internal sealed class ChatConfig
    {
        public List<string> StopSequences { get; init; } = new();
        public string System { get; init; } = "";
        public int MaxTokens { get; init; } = 4096; // cited web answers are long
        public Model Model { get; init; } = Model.ClaudeHaiku4_5;
        public WebSearchOptions? WebSearch { get; init; } // null: web search unavailable
    }

    internal class Program
    {
        static async Task Main(string[] args)
        {
            DotNetEnv.Env.Load();

            AnthropicClient client;
            try
            {
                // Strict validation rejects basic web search responses: the SDK requires a "caller" field on
                // server_tool_use blocks that web_search_20250305 does not send.
                client = new() { ResponseValidation = false };
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error initializing Anthropic client: {ex.Message}");
                return;
            }
            Console.WriteLine("Anthropic client initialized successfully.");

            WebSearchOptions? webSearch = null;
            try
            {
                webSearch = WebSearchOptions.FromEnvironment(Environment.GetEnvironmentVariable);
            }
            catch (FormatException ex)
            {
                Console.WriteLine($"Web search disabled: {ex.Message}");
            }

            ChatConfig config = new()
            {
                System = "You are patient and understanding of a developer trying to build with you. " +
            "The dev configured a continuous message loop.",
                WebSearch = webSearch,
            };
            try
            {
                await ChatService.Chat(client, config);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error during chat: {ex.Message}");
            }
        }
    }
}


