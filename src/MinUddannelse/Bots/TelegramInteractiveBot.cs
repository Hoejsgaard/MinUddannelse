using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using MinUddannelse.Configuration;
using MinUddannelse.AI.Services;
using MinUddannelse.Content.WeekLetters;

namespace MinUddannelse.Bots;

public class TelegramInteractiveBot : IDisposable
{
    private readonly Child _child;
    private readonly IOpenAiService _aiService;
    private readonly ILogger _logger;
    private readonly bool _enableInteractive;
    private ITelegramBotClient? _botClient;
    private CancellationTokenSource? _cancellationTokenSource;

    public string AssignedChildName => _child.FirstName;

    private bool _disposed;

    public TelegramInteractiveBot(
        Child child,
        IOpenAiService aiService,
        ILoggerFactory loggerFactory,
        bool enableInteractive = true)
    {
        _child = child ?? throw new ArgumentNullException(nameof(child));
        _aiService = aiService ?? throw new ArgumentNullException(nameof(aiService));
        _logger = (loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory))).CreateLogger<TelegramInteractiveBot>();
        _enableInteractive = enableInteractive;
    }

    public async Task Start()
    {
        if (string.IsNullOrEmpty(_child.Channels?.Telegram?.Token))
        {
            _logger.LogError("Cannot start Telegram bot for {ChildName}: Token is missing", _child.FirstName);
            return;
        }

        _logger.LogInformation("Starting Telegram bot for child: {ChildName}", _child.FirstName);

        try
        {
            _botClient = new TelegramBotClient(_child.Channels.Telegram.Token);

            var me = await _botClient.GetMeAsync();
            _logger.LogInformation("Telegram bot authenticated as @{BotUsername} for child {ChildName}", me.Username, _child.FirstName);

            if (_enableInteractive)
            {
                _cancellationTokenSource = new CancellationTokenSource();

                var receiverOptions = new ReceiverOptions
                {
                    AllowedUpdates = new[] { UpdateType.Message },
                    ThrowPendingUpdates = true
                };

                _botClient.StartReceiving(
                    updateHandler: HandleUpdateAsync,
                    pollingErrorHandler: HandlePollingErrorAsync,
                    receiverOptions: receiverOptions,
                    cancellationToken: _cancellationTokenSource.Token
                );

                _logger.LogInformation("Telegram bot started successfully for {ChildName}", _child.FirstName);
            }
            else
            {
                _logger.LogInformation("Telegram interactive polling disabled - bot will only send messages for {ChildName}", _child.FirstName);
            }

            if (_child.Channels.Telegram.ChatId.HasValue && _child.Channels.Telegram.SendStartupMessage)
            {
                await SendMessageToTelegram($"Bot for {_child.FirstName} is now online and ready to help!");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start Telegram bot for {ChildName}", _child.FirstName);
            throw;
        }
    }

    public void Stop()
    {
        _logger.LogInformation("Telegram bot stopped for {ChildName}", _child.FirstName);
        _cancellationTokenSource?.Cancel();
    }

    public async Task SendMessageToTelegram(string message)
    {
        if (_botClient == null)
        {
            _logger.LogWarning("Cannot send message: Telegram bot not initialized");
            return;
        }

        if (_child.Channels?.Telegram?.ChatId == null)
        {
            _logger.LogWarning("Cannot send message for {ChildName}: ChatId not configured", _child.FirstName);
            return;
        }

        try
        {
            await _botClient.SendTextMessageAsync(
                chatId: _child.Channels.Telegram.ChatId.Value,
                text: message,
                parseMode: ParseMode.Html,
                cancellationToken: default
            );

            _logger.LogInformation("Message sent successfully to Telegram for {ChildName}", _child.FirstName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send Telegram message for {ChildName}", _child.FirstName);
        }
    }

    private async Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
    {
        if (update.Message is not { } message)
            return;

        if (message.Type != MessageType.Text || string.IsNullOrEmpty(message.Text))
            return;

        var chatId = message.Chat.Id;
        var messageText = message.Text.Trim();

        _logger.LogInformation("Processing message from {ChatId} for child {ChildName}: {Text}",
            chatId, _child.FirstName, messageText);

        try
        {
            if (IsHelpCommand(messageText))
            {
                await SendHelpMessage(botClient, chatId, cancellationToken);
                return;
            }

            string contextKey = $"telegram-{_child.FirstName}-{chatId}";
            string? response = await _aiService.GetResponseWithContextAsync(_child, messageText, contextKey);

            if (string.IsNullOrEmpty(response))
            {
                response = "I couldn't process your request. Please try again.";
            }

            await botClient.SendTextMessageAsync(
                chatId: chatId,
                text: response,
                parseMode: ParseMode.Html,
                cancellationToken: cancellationToken
            );

            _logger.LogInformation("Sent response to Telegram chat {ChatId} for child {ChildName}", chatId, _child.FirstName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing message for child {ChildName}: {Message}", _child.FirstName, messageText);

            await botClient.SendTextMessageAsync(
                chatId: chatId,
                text: "Sorry, I encountered an error processing your message. Please try again.",
                cancellationToken: cancellationToken
            );
        }
    }

    private Task HandlePollingErrorAsync(ITelegramBotClient botClient, Exception exception, CancellationToken cancellationToken)
    {
        var errorMessage = exception switch
        {
            ApiRequestException apiRequestException =>
                $"Telegram API Error for {_child.FirstName}: [{apiRequestException.ErrorCode}] {apiRequestException.Message}",
            _ => exception.ToString()
        };

        _logger.LogError(exception, "Telegram polling error for {ChildName}: {Error}", _child.FirstName, errorMessage);

        return Task.CompletedTask;
    }

    private static bool IsHelpCommand(string text)
    {
        var normalized = text.Trim().ToLowerInvariant();
        return normalized == "help" || normalized == "--help" || normalized == "?" ||
               normalized == "commands" || normalized == "/help" || normalized == "/start" ||
               normalized == "hjælp" || normalized == "kommandoer" || normalized == "/hjælp";
    }

    private async Task SendHelpMessage(ITelegramBotClient botClient, long chatId, CancellationToken cancellationToken)
    {
        var helpMessage = $@"**Aula Bot Help for {_child.FirstName}**

I can help you with information about {_child.FirstName}'s school activities from Aula.

**Commands:**
• Ask about activities: ""What activities does {_child.FirstName} have this week?""
• Get week letters: ""Show me this week's letter""
• Get homework info: ""What homework is there?""

**Languages:** You can ask in both English and Danish.

**Tips:**
• I can remember the context of our conversation
• Use natural language - no need for special commands";

        await botClient.SendTextMessageAsync(
            chatId: chatId,
            text: helpMessage,
            parseMode: ParseMode.Markdown,
            cancellationToken: cancellationToken
        );
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _cancellationTokenSource?.Cancel();
        _cancellationTokenSource?.Dispose();
        _disposed = true;
    }
}
