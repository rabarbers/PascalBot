using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace PascalBot
{
    public static class Helper
    {
        public static async Task<Dictionary<string, uint>> LoadAccountsAsync(string fileName)
        {
            if (File.Exists(fileName))
            {
                var text = await File.ReadAllTextAsync(fileName);
                return JsonSerializer.Deserialize<Dictionary<string, uint>>(text);
            }
            return new Dictionary<string, uint>();
        }

        public static Task SaveAccountsAsync(string fileName, Dictionary<string, uint> accounts)
        {
            var text = JsonSerializer.Serialize(accounts);
            return File.WriteAllTextAsync(fileName, text);
        }

        public static async Task<List<ulong>> LoadPasaReceiversAsync(string fileName)
        {
            if (File.Exists(fileName))
            {
                var text = await File.ReadAllTextAsync(fileName);
                return JsonSerializer.Deserialize<List<ulong>>(text);
            }
            return new List<ulong>();
        }

        public static Task SavePasaReceiversAsync(string fileName, List<ulong> users)
        {
            var text = JsonSerializer.Serialize(users);
            return File.WriteAllTextAsync(fileName, text);
        }

        public static bool IsValidPasa(string pasaString, out uint validPasa)
        {
            validPasa = 0;
            var res = pasaString.Split("-");
            if (res.Length < 1)
            {
                return false;
            }
            if (uint.TryParse(res[0], out var account))
            {
                if (res.Length == 1)
                {
                    validPasa = account;
                    return true;
                }
                if (res.Length == 2 && res[1].Length == 2 && uint.TryParse(res[1], out var checksum))
                {
                    var calculatedChecksum = (account * 101 % 89 + 10) % 256;
                    var result = calculatedChecksum == checksum;
                    validPasa = result ? account : 0;
                    return result;
                }
            }
            return false;
        }

        public static string GetFullPasa(uint account)
        {
            var calculatedChecksum = (account * 101 % 89 + 10) % 256;
            return $"{account}-{calculatedChecksum}";
        }
    }
}
