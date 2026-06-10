namespace WhaleTracker.API.Configuration;

public static class EnvFileLoader
{
    private static readonly Dictionary<string, string> ConfigurationKeyMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["POSTGRES_CONNECTION"] = "ConnectionStrings__DefaultConnection",
        ["ZERION_API_KEY"] = "Zerion__ApiKey",
        ["ZERION_WHALE_ADDRESS"] = "Zerion__WhaleAddress",
        ["GROQ_API_KEY"] = "Groq__ApiKey",
        ["GROQ_MODEL"] = "Groq__Model",
        ["GROQ_BASE_URL"] = "Groq__BaseUrl",
        ["OPENAI_API_KEY"] = "OpenAi__ApiKey",
        ["OKX_API_KEY"] = "Okx__ApiKey",
        ["OKX_SECRET_KEY"] = "Okx__SecretKey",
        ["OKX_PASSPHRASE"] = "Okx__Passphrase",
        ["OKX_IS_DEMO"] = "Okx__IsDemo",
        ["OKX_BASE_URL"] = "Okx__BaseUrl",
        ["ADMIN_USER"] = "Auth__AdminUser",
        ["ADMIN_PASSWORD"] = "Auth__AdminPassword"
    };

    public static void LoadNearest(string startDirectory)
    {
        var directory = new DirectoryInfo(startDirectory);

        while (directory != null)
        {
            var path = Path.Combine(directory.FullName, ".env");
            if (File.Exists(path))
            {
                Load(path);
                return;
            }

            directory = directory.Parent;
        }
    }

    private static void Load(string path)
    {
        foreach (var rawLine in File.ReadAllLines(path))
        {
            var line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
            {
                continue;
            }

            var separatorIndex = line.IndexOf('=');
            if (separatorIndex <= 0)
            {
                continue;
            }

            var key = line[..separatorIndex].Trim();
            var value = line[(separatorIndex + 1)..].Trim().Trim('"', '\'');

            Environment.SetEnvironmentVariable(key, value);

            if (ConfigurationKeyMap.TryGetValue(key, out var configurationKey))
            {
                Environment.SetEnvironmentVariable(configurationKey, value);
            }
        }
    }
}
