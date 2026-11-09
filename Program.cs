using System;
using DotNetEnv;
using Anthropic;
using Anthropic.Models.Messages;

namespace ToolUseLab
{

    internal sealed class ChatConfig
    {
        public List<string> StopSequences { get; init; } = new();
        public string System { get; init; } = "";
        public int MaxTokens { get; init; } = 1024;
        public Model Model { get; init; } = Model.ClaudeHaiku4_5;

        public bool IsChatStream { get; init; }

        public WebSearchOptions? WebSearch { get; init; } // null: web search unavailable

        public RagSession? Rag { get; init; } // null: document search unavailable
    }

    internal class Program
    {
        static async Task Main(string[] args)
        {
            DotNetEnv.Env.Load();

            WebSearchOptions? webSearch = null;
            try
            {
                webSearch = WebSearchOptions.FromEnvironment(Environment.GetEnvironmentVariable);
            }
            catch (FormatException ex)
            {
                Console.WriteLine($"Web search disabled: {ex.Message}");
            }

            // No VOYAGE_API_KEY means the user does not use RAG: stay quiet. A key with bad RAG_* values is worth a message.
            RagSession? rag = null;
            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("VOYAGE_API_KEY")))
            {
                try
                {
                    var ragOptions = RagOptions.FromEnvironment(Environment.GetEnvironmentVariable);
                    var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
                    rag = new RagSession(ragOptions, new VoyageEmbedder(http, ragOptions.ApiKey, ragOptions.Model));
                }
                catch (FormatException ex)
                {
                    Console.WriteLine($"RAG disabled: {ex.Message}");
                }
            }

            AnthropicClient client;
            try
            {
                // Strict validation rejects basic web search responses: the SDK requires a "caller" field on
                // server_tool_use blocks that web_search_20250305 does not send. So it is on only without search.
                client = new() { ResponseValidation = webSearch is null };
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error initializing Anthropic client: {ex.Message}");
                return;
            }
            Console.WriteLine("Anthropic client initialized successfully.");

            ChatConfig config = new()
            {
                System = "You are patient and understanding of a developer trying to build with you. " +
            "The dev configured a continuous message loop.",
                IsChatStream = false,
                WebSearch = webSearch,
                Rag = rag,
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


