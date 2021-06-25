using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Configuration;
using Pascal.Wallet.Connector;
using Pascal.Wallet.Connector.DTO;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PascalBot
{
    class Program
    {
        private const string AccountsFile = "accounts.txt";
        private const string PasaReceiversFile = "pasareceivers.txt";
        private const decimal RegisteredPasaReward = 0.001M;
        private const decimal UnRegisteredPasaReward = 0.0009M;
        private const string DefaultMessage = @"Wrong command or command parameters!
To get started type the command: !start
To get help, type the command: !help";
        private const string StartMessage = @"PascalBot rewards discord activity with cryptocurrency. 

You will get 0.001 Pascal for every message you write, mention and thumbs up you get.
To get started follow these steps:
1. Download the Blaise wallet
Android
https://play.google.com/store/apps/details?id=com.appditto.blaise
Apple iOS
https://apps.apple.com/us/app/blaise-pascal-wallet/id1473011216

Follow the on screen guide on how to set it up.

2. Write down or copy you account number(PASA) from the blaise wallet

3.1. SET your PASA name to match your discord username. Instructions are here: https://pascalcoinblockchain.medium.com/how-to-set-up-your-pascal-account-to-receive-airdrops-from-our-discord-tip-bot-1712058366bb
3.2. If You cannot set your PASA name, the alternative method is to use in command !setmypasa in the @PascalBot private chat to set your account so that PascalBot knows where to send your Pascal Coins.
For example:
!setmypasa 1141769-44
If you set your PASA name to match your discord name, then the amount of message and mention reward will be 0.001 Pascals. If you use bot command !setmypasa, then the amount of each reward will be 0.0009 Pascals.

4. You are all set. Now you will recive 0.001 or 0.0009 Pascal for ever message you write, mention and thumbs up you get.

Use !help to see all other commands.";
        private const string HelpMessage = @"Here are all the commands you can use with PascalBot

!start
Get started guide

!setmypasa AccountNumber
where AccountNumber is your PASA. For example:
!setmypasa 1141769-44

To check your registered account number(PASA) in PascalBot use command:
!mypasa

To unregister account number(PASA) from PascalBot use command:
!removemypasa

To check other user PASA use command:
!showpasa Username
Where Username is Discord username or PASA name in Pascal network (Discord username has higher priority).

To get your first pasa use command:
!getfirstpasa PUBLIC_KEY
Where PUBLIC_KEY is in b58 format (Pascal Wallet exports keys in b58 format).

To get information about available PASA in PascalBot use command:
!info";
        private static IConfiguration _config;
        private static PascalConnector _pascalWallet;
        private static Dictionary<string, uint> _accounts;
        private static readonly Dictionary<string, string> _history = new();
        private static List<string> _blacklistedDiscordUsers = new();
        private static List<string> _blacklistedPublicKeys = new();
        private static List<ulong> _pasaReceivers = new();
        private static string _pasaDistributionPublicKey;
        private static readonly SemaphoreSlim _lock = new(1, 1);

        static async Task Main()
        {
            _config = new ConfigurationBuilder().AddJsonFile("PascalBot.json", true, true).Build();

            var index = 0;
            while(true)
            {
                var blacklistedUser = _config[$"BlacklistedDiscordUsers:{index++}"];
                if(blacklistedUser == null)
                {
                    break;
                }
                _blacklistedDiscordUsers.Add(blacklistedUser.ToLower());
            }
            index = 0;
            while (true)
            {
                var blacklistedPublicKey = _config[$"BlacklistedPublicKeys:{index++}"];
                if (blacklistedPublicKey == null)
                {
                    break;
                }
                _blacklistedPublicKeys.Add(blacklistedPublicKey.ToUpper());
            }

            _pasaReceivers = await Helper.LoadPasaReceiversAsync(PasaReceiversFile);

            _pasaDistributionPublicKey = _config[$"PasaDistributionPublicKey"];

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

            await _lock.WaitAsync();
            try
            {
                var pascalUserName = discordMessage.Author.Username.ToLower();
                var serverName = (discordMessage.Channel as SocketGuildChannel)?.Guild.Name;
                var sameAuthor = discordMessage.Author.Id == arg3.UserId;
                if (!discordMessage.Content.Trim().StartsWith("!") && arg3.Emote.Name == "👍" && !sameAuthor)
                {
                    await SendPascAsync(pascalUserName, $"A Discord user likes your activity in server {serverName} channel {discordMessage.Channel.Name}.", serverName, discordMessage.Channel.Name);
                }
            }
            finally
            {
                _lock.Release();
            }
        }

        private static async Task ClientMessageReceivedAsync(SocketMessage discordMessage)
        {
            if (discordMessage.Author.IsBot)
            {
                return;
            }

            await _lock.WaitAsync();
            try
            {
                var discordUserName = discordMessage.Author.Username;
                var discordUserId = discordMessage.Author.Id;
                var pascalUserName = discordMessage.Author.Username.ToLower();
                var serverName = (discordMessage.Channel as SocketGuildChannel)?.Guild.Name;

                if (!string.IsNullOrEmpty(serverName))
                {
                    //message in public channel
                    var trimmedContent = discordMessage.Content.Trim();
                    if (trimmedContent.StartsWith("!") || trimmedContent.StartsWith("$")) //ignore bot commands
                    {
                        return;
                    }

                    _history.TryGetValue(discordMessage.Channel.Name, out var previousChannelAuthor);
                    if (previousChannelAuthor != discordUserName)
                    {
                        _history[discordMessage.Channel.Name] = discordUserName;
                        var hasSent = await SendPascAsync(pascalUserName, $"Discord activity in server {serverName} channel {discordMessage.Channel.Name}.", serverName, discordMessage.Channel.Name);
                        //if the bot does not know how to send pasc to the user AND the user have no history chatting with the bot THEN inform the user about PASA registration
                        var privateChannel = await discordMessage.Author.GetOrCreateDMChannelAsync();
                        if (!hasSent && (await privateChannel.GetMessagesAsync(1, CacheMode.AllowDownload).FlattenAsync()).Count() == 0)
                        {
                            await privateChannel.SendMessageAsync(StartMessage);
                        }
                    }
                    foreach (var mentionedUser in discordMessage.MentionedUsers.Where(n => !n.IsBot).Select(n => n.Username.ToLower()).Distinct().Where(n => n != pascalUserName))
                    {
                        await SendPascAsync(mentionedUser, $"A Discord user {discordUserName} mentioned you in server {serverName} channel {discordMessage.Channel.Name}.", serverName, discordMessage.Channel.Name);
                    }
                }

                //message in private channel
                if (string.IsNullOrEmpty(serverName) && discordMessage.Channel.Name.StartsWith("@"))
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
                    if (parts.Length >= 1 && parts[0].ToLower() == "!start")
                    {
                        await discordMessage.Channel.SendMessageAsync(StartMessage);
                        return;
                    }
                    if (parts.Length >= 1 && parts[0].ToLower() == "!help")
                    {
                        await discordMessage.Channel.SendMessageAsync(HelpMessage);
                        return;
                    }
                    if (parts.Length >= 2 && parts[0].ToLower() == "!getfirstpasa")
                    {
                        var pubKey = parts[1];

                        if (_blacklistedPublicKeys.Contains(pubKey.ToUpper()) || _blacklistedDiscordUsers.Contains(discordUserName.ToLower()))
                        {
                            await discordMessage.Channel.SendMessageAsync($"You have enough Pascal accounts, consider donating some of them to Pascal bot public key: {_pasaDistributionPublicKey}");
                            return;
                        }

                        var keyAccountsResponse = await _pascalWallet.FindAccountsAsync(b58PublicKey: pubKey);
                        if (keyAccountsResponse.Result != null && keyAccountsResponse.Result.Length > 0 || _pasaReceivers.Contains(discordUserId))
                        {
                            await discordMessage.Channel.SendMessageAsync("You already have PASA. This service is intended for new users who do not have any PASA.");
                            return;
                        }

                        var accountsResponse = await _pascalWallet.GetWalletAccountsAsync(b58PublicKey: _pasaDistributionPublicKey);
                        var pendingsResponse = await _pascalWallet.GetPendingsAsync(max: 12000);
                        if (accountsResponse.Result != null && accountsResponse.Result.Length > 0 && pendingsResponse.Result != null)
                        {
                            uint? accountToGive = accountsResponse.Result
                                .FirstOrDefault(accountObj => pendingsResponse.Result.All(pendingsObj => pendingsObj.Changers.All(changer => changer.AccountNumber != accountObj.AccountNumber)))?.AccountNumber;
                            if (accountToGive.HasValue)
                            {
                                var changeKeyResponse = await _pascalWallet.ChangeKeyAsync(accountToGive.Value, newB58PublicKey: pubKey);
                                if(changeKeyResponse.Result != null)
                                {
                                    _pasaReceivers.Add(discordUserId);
                                    await Helper.SavePasaReceiversAsync(PasaReceiversFile, _pasaReceivers);
                                }
                                var message = changeKeyResponse.Result != null
                                    ? $"Account {accountToGive} assinged to your public key. Operation ophash = {changeKeyResponse.Result.OpHash}. Wait ~5 minutes till the operation is included in the blockchain."
                                    : changeKeyResponse.Error.Message;
                                await discordMessage.Channel.SendMessageAsync(message);
                            }
                        }
                        var failureMessage = accountsResponse.Result != null || pendingsResponse != null ? "PascalBot has run out of free accounts. Try again after some time..." : "PascalBot cannot reach Pascal wallet.";
                        await discordMessage.Channel.SendMessageAsync(failureMessage);
                        return;
                    }
                    if (parts.Length >= 1 && parts[0].ToLower() == "!info")
                    {
                        string message;
                        var accountsCountResponse = await _pascalWallet.GetWalletAccountsCountAsync(b58PublicKey: _pasaDistributionPublicKey);
                        if (accountsCountResponse.Result != null)
                        {
                            message = $"PascalBot has {accountsCountResponse.Result ?? 0} accounts for new users. If you want to donate PASA, change PASA public key to: {_pasaDistributionPublicKey}";
                        }
                        else
                        {
                            message = accountsCountResponse.Error.Message;
                        }
                        await discordMessage.Channel.SendMessageAsync(message);
                        return;
                    }
                    await discordMessage.Channel.SendMessageAsync(DefaultMessage);
                }
            }
            finally
            {
                _lock.Release();
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

        private static async Task<bool> SendPascAsync(string receiverAccount, string message, string server, string channel)
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

            var senderAccount = server == _config["DiscordServerName"] ? uint.Parse(_config["PascalBotPASA"]) : uint.Parse(_config["PascalBotPASAForNonPascalServers"]);
            var transactionResponse = await _pascalWallet.SendToAsync(senderAccount: senderAccount, receiverAccount: receiverAccountNumber,
                fee: 0.0001M, amount: reward, payload: message, payloadMethod: PayloadMethod.None);

            var logMessage = transactionResponse.Result != null
                ? $"Server: {server}. Successfully sent tip to PASA named: {receiverAccount}. Operation details: {transactionResponse.Result.Description}"
                : $"Failed to send transaction to '{receiverAccount}': {transactionResponse.Error.Message}";
            Console.WriteLine(logMessage);
            return true;
        }
    }
}
