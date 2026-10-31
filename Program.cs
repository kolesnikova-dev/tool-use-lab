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
        public int MaxTokens { get; init; } = 1024;
        public Model Model { get; init; } = Model.ClaudeHaiku4_5;

        public bool IsChatStream { get; init; }
    }

    internal class Program
    {
        static async Task Main(string[] args)
        {
            DotNetEnv.Env.Load();

            AnthropicClient client;
            try
            {
                client = new() { ResponseValidation = true };
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
                isChatStream = false
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


