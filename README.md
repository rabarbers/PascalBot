# PascalBot
Discord bot for PascalCoin Discord server. Bot listens for Discord messages in Discord Pascal server and sends rewards to user Pascal accounts for every user message, mention and thumbs-up reactions.

## Dependencies
Bot connects to PascalCoin Wallet using JSON RPC https://github.com/PascalCoin/PascalCoin/releases

## Connection to Pascal network
.NET5 library to call Pascal full node Wallet JSON RPC API methods, Nuget package: https://www.nuget.org/packages/Pascal.Wallet.Connector
## Connection to Discord
Nuget package: https://www.nuget.org/packages/Discord.Net/
## Configuration
Configuration is stored in file PascalBot.json.
