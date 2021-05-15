using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Configuration;
using Pascal.Wallet.Connector;
using Pascal.Wallet.Connector.DTO;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace PascalBot
{
    class Program
    {
        private const string AccountsFile = "accounts.txt";
        private const decimal RegisteredPasaReward = 0.001M;
        private const decimal UnRegisteredPasaReward = 0.0009M;
        private const string InfoMessage = @"PascalBot rewards discord activity (messages, mentions, thumbs up reactions). To participate you need to
1) have Pascal account (PASA) with the same name as your discord username (instructions here: https://pascalcoinblockchain.medium.com/how-to-set-up-your-pascal-account-to-receive-airdrops-from-our-discord-tip-bot-1712058366bb )
or
2) call PascalBot command in PascalBot private chat:
!setmypasa AccountNumber
where AccountNumber is your PASA. For example:
!setmypasa 1141769-44

To check your registered PASA in PascalBot use command:
!mypasa

To unregister PASA from PascalBot use command:
!removemypasa

To check other user PASA use command:
!showpasa Username
Where Username is Discord username or PASA name in Pascal network (Discord username has higher priority).

Advice: reserve your PASA name in Pascal network then your reward for discord messages and mentions will be 0.001 Pasc. If you register your account with PascalBot using commands, then your reward will be 0.0009 Pasc. If you send many small messages together, bot interprets it as one big message and there is only one reward for it. Duplicate mentions in one message are intrerpeted as one reward.";
        private static IConfiguration _config;
        private static PascalConnector _pascalWallet;
        private static Dictionary<string, uint> _accounts;
        private static readonly Dictionary<string, string> _history = new Dictionary<string, string>();

        static async Task Main()
        {
            _config = new ConfigurationBuilder().AddJsonFile("PascalBot.json", true, true).Build();
            var zzz = _config["DiscordBotToken"];

            _accounts = await Helper.LoadAccountsAsync(AccountsFile);

            _pascalWallet = new PascalConnector(address: _config["PascalWalletAddress"], port: uint.Parse(_config["PascalWalletPort"]));

            var discord = new DiscordSocketClient();

            discord.Log += LogAsync;
            discord.MessageReceived += ClientMessageReceivedAsync;
            discord.ReactionAdded += DiscordReactionAdded;

            await discord.LoginAsync(TokenType.Bot, _config["DiscordBotToken"]);
            await discord.StartAsync();

            await Task.Delay(-1);
        }

        private static async Task DiscordReactionAdded(Cacheable<IUserMessage, ulong> arg1, ISocketMessageChannel arg2, SocketReaction arg3)
        {
            var discordMessage = await arg1.GetOrDownloadAsync();
            if (discordMessage.Author.IsBot)
            {
                return;
            }

            var pascalUserName = discordMessage.Author.Username.ToLower();
            var serverName = (discordMessage.Channel as SocketGuildChannel)?.Guild.Name;
            var sameAuthor = discordMessage.Author.Id == arg3.UserId;
            if (serverName == _config["DiscordServerName"] && !discordMessage.Content.Trim().StartsWith("!") && arg3.Emote.Name == "👍" && !sameAuthor)
            {
                await SendPascAsync(pascalUserName, $"Some Discord user likes your activity in channel {discordMessage.Channel.Name}.", discordMessage.Channel.Name);
            }
        }

        private static async Task ClientMessageReceivedAsync(SocketMessage discordMessage)
        {
            if (discordMessage.Author.IsBot)
            {
                return;
            }

            var discordUserName = discordMessage.Author.Username;
            var pascalUserName = discordMessage.Author.Username.ToLower();
            var serverName = (discordMessage.Channel as SocketGuildChannel)?.Guild.Name;

            //message in Pascal server
            if (serverName == _config["DiscordServerName"])
            {
                var trimmedContent = discordMessage.Content.Trim();
                if (trimmedContent.StartsWith("!") || trimmedContent.StartsWith("$")) //ignore bot commands
                {
                    return;
                }

                _history.TryGetValue(discordMessage.Channel.Name, out var previousChannelAuthor);
                if (previousChannelAuthor != discordUserName)
                {
                    _history[discordMessage.Channel.Name] = discordUserName;
                    var hasSent = await SendPascAsync(pascalUserName, $"Discord activity in channel {discordMessage.Channel.Name}.", discordMessage.Channel.Name);
                    //if the bot does not know how to send pasc to the user AND the user have no history chatting with the bot THEN inform the user about PASA registration
                    var privateChannel = await discordMessage.Author.GetOrCreateDMChannelAsync();
                    if (!hasSent && (await privateChannel.GetMessagesAsync(1, CacheMode.AllowDownload).FlattenAsync()).Count() == 0)
                    {
                        await privateChannel.SendMessageAsync(InfoMessage);
                    }
                }
                foreach (var mentionedUser in discordMessage.MentionedUsers.Where(n => !n.IsBot).Select(n => n.Username.ToLower()).Distinct().Where(n => n != pascalUserName))
                {
                    await SendPascAsync(mentionedUser, $"Discord user {discordUserName} mentioned you in channel {discordMessage.Channel.Name}.", discordMessage.Channel.Name);
                }
            }

            //message in private channel
            if (discordMessage.Channel.Name.StartsWith("@"))
            {
                var parts = discordMessage.Content.Trim().Split(" ");
                if (parts.Length >= 2 && parts[0].ToLower() == "!setmypasa")
                {
                    if (!Helper.IsValidPasa(parts[1], out var newAccountNumber))
                    {
                        await discordMessage.Channel.SendMessageAsync($"Invalid PASA provided!");
                        return;
                    }
                    if (!await AccountExistsAsync(newAccountNumber))
                    {
                        await discordMessage.Channel.SendMessageAsync($"Provided PASA does not exist on the blockchain!");
                        return;
                    }

                    _accounts[pascalUserName] = newAccountNumber;
                    await discordMessage.Channel.SendMessageAsync($"{discordUserName} Pascal account (PASA) set to {newAccountNumber}.");
                    await Helper.SaveAccountsAsync(AccountsFile, _accounts);
                    return;
                }
                if (parts.Length >= 1 && parts[0].ToLower() == "!mypasa")
                {
                    string operationMessage;
                    if (_accounts.TryGetValue(pascalUserName, out var accountNumber))
                    {
                        operationMessage = $"{discordUserName} Pascal account (PASA) is {Helper.GetFullPasa(accountNumber)}.";
                    }
                    else
                    {
                        var targetAccountResponse = await _pascalWallet.FindAccountsAsync(name: pascalUserName);
                        operationMessage = targetAccountResponse.Result != null && targetAccountResponse.Result.Length == 1
                            ? $"{discordUserName} Pascal account (PASA) is {targetAccountResponse.Result[0].AccountNumber}."
                            : $"{discordUserName} is not PASA name and you have not registered PASA in PascalBot.";
                    }
                    await discordMessage.Channel.SendMessageAsync(operationMessage);
                    return;
                }
                if (parts.Length == 2 && parts[0].ToLower() == "!showpasa")
                {
                    string operationMessage;
                    var mentionedUser = parts[1];
                    if (mentionedUser != null)
                    {
                        if (_accounts.TryGetValue(mentionedUser.ToLower(), out var accountNumber))
                        {
                            var account = await _pascalWallet.GetAccountAsync(accountNumber);
                            var balance = account.Result != null ? $", balance: { account.Result.Balance} Pasc" : string.Empty;
                            operationMessage = $"User {mentionedUser} PASA is {Helper.GetFullPasa(accountNumber)} (registered in Discord){balance}.";
                        }
                        else
                        {
                            var targetAccountResponse = await _pascalWallet.FindAccountsAsync(name: mentionedUser.ToLower());

                            var balance = targetAccountResponse.Result != null && targetAccountResponse.Result.Length == 1 ? $", balance: { targetAccountResponse.Result[0].Balance} Pasc" : string.Empty;
                            operationMessage = targetAccountResponse.Result != null && targetAccountResponse.Result.Length == 1
                                ? $"User {mentionedUser} PASA is {Helper.GetFullPasa(targetAccountResponse.Result[0].AccountNumber)} (registered in Pascal network){balance}."
                                : $"User {mentionedUser} have not registered his PASA.";
                        }
                    }
                    else
                    {
                        operationMessage = "Command !showpasa requires one parameter - user mention.";
                    }
                    await discordMessage.Channel.SendMessageAsync(operationMessage);
                    return;
                }
                if (parts.Length >= 1 && parts[0].ToLower() == "!removemypasa")
                {
                    var message = _accounts.Remove(discordUserName.ToLower())
                        ? $"PascalBot removed '{discordUserName}' Pascal account from the memory."
                        : $"Cannot remove what is not registered.";
                    await discordMessage.Channel.SendMessageAsync(message);
                    await Helper.SaveAccountsAsync(AccountsFile, _accounts);
                    return;
                }
                await discordMessage.Channel.SendMessageAsync(InfoMessage);
            }
        }

        private static Task LogAsync(LogMessage msg)
        {
            Console.WriteLine(msg.ToString());
            return Task.CompletedTask;
        }

        private static async Task<bool> AccountExistsAsync(uint newAccount)
        {
            var accountResponse = await _pascalWallet.GetAccountAsync(newAccount);
            return accountResponse.Result != null;
        }

        private static async Task<bool> SendPascAsync(string receiverAccount, string message, string channel)
        {
            var reward = UnRegisteredPasaReward;
            if (!_accounts.TryGetValue(receiverAccount, out var receiverAccountNumber))
            {
                reward = RegisteredPasaReward;
                var targetAccountResponse = await _pascalWallet.FindAccountsAsync(name: receiverAccount);
                if (targetAccountResponse.Result != null && targetAccountResponse.Result.Length == 1)
                {
                    receiverAccountNumber = targetAccountResponse.Result[0].AccountNumber;
                }
                else
                {
                    Console.WriteLine($"PASA account name '{receiverAccount}' is not registered. Activity in channel: {channel}");
                    return false;
                }
            }

            var transactionResponse = await _pascalWallet.SendToAsync(senderAccount: uint.Parse(_config["PascalBotPASA"]), receiverAccount: receiverAccountNumber,
                fee: 0.0001M, amount: reward, payload: message, payloadMethod: PayloadMethod.None);

            var logMessage = transactionResponse.Result != null
                ? $"Successfully sent tip to PASA named: {receiverAccount}. Operation details: {transactionResponse.Result.Description}"
                : $"Failed to send transaction to '{receiverAccount}': {transactionResponse.Error.Message}";
            Console.WriteLine(logMessage);
            return true;
        }
    }
}
