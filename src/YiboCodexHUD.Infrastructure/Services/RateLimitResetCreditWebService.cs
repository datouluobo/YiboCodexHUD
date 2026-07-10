using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using YiboCodexHUD.Core.Utilities;
using YiboCodexHUD.Infrastructure.Models;

namespace YiboCodexHUD.Infrastructure.Services;

public sealed class RateLimitResetCreditWebService
{
    private const string ResetCreditsEndpoint = "https://chatgpt.com/backend-api/wham/rate-limit-reset-credits";
    private const int BlockFileHeaderSize = 8192;
    private const int EntryStoreSize = 256;
    private const int EntryKeyOffset = 96;
    private const int EntryKeyInlineMaxLength = 160;

    private static readonly JsonSerializerOptions JsonSerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly ILogger<RateLimitResetCreditWebService> _logger;

    public RateLimitResetCreditWebService(ILogger<RateLimitResetCreditWebService> logger)
    {
        _logger = logger;
    }

    internal async Task<CodexRateLimitResetCredits?> TryFetchAsync(CancellationToken cancellationToken)
    {
        foreach (var profileDirectory in CodexDesktopIdentity.GetProfileDirectoryCandidates())
        {
            try
            {
                var cachedResetCredits = TryReadFromBrowserCache(profileDirectory);
                if (cachedResetCredits is not null)
                {
                    _logger.LogInformation("Read rate-limit reset credits from Chromium cache in profile {ProfileDirectory}.", profileDirectory);
                    return cachedResetCredits;
                }

                var cookieContainer = await TryCreateCookieContainerAsync(profileDirectory, cancellationToken);
                if (cookieContainer is null)
                {
                    continue;
                }

                var resetCredits = await TryFetchFromEndpointAsync(cookieContainer, cancellationToken);
                if (resetCredits is not null)
                {
                    _logger.LogInformation("Fetched rate-limit reset credits from web endpoint using profile {ProfileDirectory}.", profileDirectory);
                    return resetCredits;
                }
            }
            catch (Exception exception)
            {
                _logger.LogDebug(exception, "Failed to fetch reset credits from profile {ProfileDirectory}.", profileDirectory);
            }
        }

        return null;
    }

    private CodexRateLimitResetCredits? TryReadFromBrowserCache(string profileDirectory)
    {
        if (!TryResolveCacheDirectory(profileDirectory, out var cacheDirectory))
        {
            return null;
        }

        foreach (var cacheDataFilePath in EnumerateCacheDataFilePaths(cacheDirectory))
        {
            try
            {
                var bytes = SharedReadAllBytes(cacheDataFilePath);
                var entry = TryFindCacheEntry(bytes, ResetCreditsEndpoint);
                if (entry is null)
                {
                    continue;
                }

                var headersBytes = TryReadCacheStream(cacheDirectory, entry.Value.HeadersAddress, entry.Value.HeadersSize);
                var payloadBytes = TryReadCacheStream(cacheDirectory, entry.Value.PayloadAddress, entry.Value.PayloadSize);
                if (headersBytes is null || payloadBytes is null)
                {
                    continue;
                }

                var headersText = ExtractHttpHeaders(headersBytes);
                var responseText = DecodePayload(payloadBytes, headersText);
                if (string.IsNullOrWhiteSpace(responseText))
                {
                    continue;
                }

                var resetCredits = TryDeserializeResetCredits(responseText);
                if (LooksUsable(resetCredits))
                {
                    return resetCredits;
                }
            }
            catch (Exception exception)
            {
                _logger.LogDebug(exception, "Failed to parse reset credits cache entry from {CacheDataFilePath}.", cacheDataFilePath);
            }
        }

        return null;
    }

    private async Task<CodexRateLimitResetCredits?> TryFetchFromEndpointAsync(CookieContainer cookieContainer, CancellationToken cancellationToken)
    {
        using var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            CookieContainer = cookieContainer,
            UseCookies = true
        };

        using var client = new HttpClient(handler);
        using var request = new HttpRequestMessage(HttpMethod.Get, ResetCreditsEndpoint);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("OAI-Language", CultureInfo.CurrentUICulture.Name);
        request.Headers.TryAddWithoutValidation("Origin", "app://codex");
        request.Headers.TryAddWithoutValidation("Referer", "app://codex/");
        request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 CodexDesktop/1.0");
        request.Headers.TryAddWithoutValidation("originator", "Codex Desktop");

        using var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var responsePreview = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogWarning(
                "Reset credits endpoint returned HTTP {StatusCode}. Response preview: {ResponsePreview}",
                (int)response.StatusCode,
                Truncate(responsePreview, 400));
            return null;
        }

        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
        var resetCredits = TryDeserializeResetCredits(responseText);
        if (resetCredits is null)
        {
            _logger.LogWarning(
                "Reset credits endpoint returned JSON that could not be parsed. Response preview: {ResponsePreview}",
                Truncate(responseText, 400));
        }

        return resetCredits;
    }

    private async Task<CookieContainer?> TryCreateCookieContainerAsync(string profileDirectory, CancellationToken cancellationToken)
    {
        if (!TryResolveProfilePaths(profileDirectory, out var localStatePath, out var cookiesPath))
        {
            return null;
        }

        var encryptionKey = await TryReadEncryptionKeyAsync(localStatePath, cancellationToken);
        if (encryptionKey is null || encryptionKey.Length == 0)
        {
            return null;
        }

        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"yibocodexhud-cookies-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        var temporaryCookiesPath = Path.Combine(temporaryDirectory, "Cookies");
        try
        {
            CopyCookieDatabaseFiles(cookiesPath, temporaryCookiesPath);

            var container = new CookieContainer();
            await using var connection = new SqliteConnection($"Data Source={temporaryCookiesPath};Mode=ReadOnly");
            await connection.OpenAsync(cancellationToken);

            await using var command = connection.CreateCommand();
            command.CommandText = """
                select host_key, name, path, value, encrypted_value
                from cookies
                where host_key like '%chatgpt.com%'
                   or host_key like '%openai.com%'
                """;

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var host = reader.IsDBNull(0) ? null : reader.GetString(0);
                var name = reader.IsDBNull(1) ? null : reader.GetString(1);
                var path = reader.IsDBNull(2) ? "/" : reader.GetString(2);
                var plainValue = reader.IsDBNull(3) ? null : reader.GetString(3);
                var encryptedValue = reader.IsDBNull(4) ? null : (byte[])reader[4];

                if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                var cookieValue = !string.IsNullOrEmpty(plainValue)
                    ? plainValue
                    : TryDecryptCookieValue(encryptedValue, encryptionKey);
                if (string.IsNullOrEmpty(cookieValue))
                {
                    continue;
                }

                try
                {
                    container.Add(new Cookie(name, cookieValue, path, NormalizeCookieDomain(host)));
                }
                catch (CookieException exception)
                {
                    _logger.LogDebug(exception, "Skipped invalid cookie {CookieName} for host {CookieHost}.", name, host);
                }
            }

            var cookieCount = container.GetAllCookies().Count;
            if (cookieCount == 0)
            {
                _logger.LogWarning("No ChatGPT/OpenAI cookies were found in profile {ProfileDirectory}.", profileDirectory);
                return null;
            }

            _logger.LogInformation("Loaded {CookieCount} ChatGPT/OpenAI cookies from profile {ProfileDirectory}.", cookieCount, profileDirectory);
            return container;
        }
        finally
        {
            TryDeleteTemporaryDirectory(temporaryDirectory);
        }
    }

    private static void CopyCookieDatabaseFiles(string sourceCookiesPath, string destinationCookiesPath)
    {
        File.Copy(sourceCookiesPath, destinationCookiesPath, overwrite: true);

        foreach (var suffix in new[] { "-wal", "-shm" })
        {
            var sourceSidecarPath = sourceCookiesPath + suffix;
            if (!File.Exists(sourceSidecarPath))
            {
                continue;
            }

            File.Copy(sourceSidecarPath, destinationCookiesPath + suffix, overwrite: true);
        }
    }

    private static bool TryResolveProfilePaths(string profileDirectory, out string localStatePath, out string cookiesPath)
    {
        foreach (var (localStateCandidate, cookiesCandidate) in EnumerateProfilePathCandidates(profileDirectory))
        {
            if (!File.Exists(localStateCandidate) || !File.Exists(cookiesCandidate))
            {
                continue;
            }

            localStatePath = localStateCandidate;
            cookiesPath = cookiesCandidate;
            return true;
        }

        localStatePath = string.Empty;
        cookiesPath = string.Empty;
        return false;
    }

    private static IEnumerable<(string LocalStatePath, string CookiesPath)> EnumerateProfilePathCandidates(string profileDirectory)
    {
        yield return (
            Path.Combine(profileDirectory, "Local State"),
            Path.Combine(profileDirectory, "Network", "Cookies"));

        yield return (
            Path.Combine(profileDirectory, "Local State"),
            Path.Combine(profileDirectory, "Default", "Network", "Cookies"));

        var parentDirectory = Directory.GetParent(profileDirectory)?.FullName;
        if (!string.IsNullOrWhiteSpace(parentDirectory))
        {
            yield return (
                Path.Combine(parentDirectory, "Local State"),
                Path.Combine(profileDirectory, "Network", "Cookies"));
        }
    }

    private static string NormalizeCookieDomain(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return string.Empty;
        }

        return host[0] == '.' ? host[1..] : host;
    }

    private static bool TryResolveCacheDirectory(string profileDirectory, out string cacheDirectory)
    {
        foreach (var candidate in new[]
        {
            Path.Combine(profileDirectory, "Default", "Cache", "Cache_Data"),
            Path.Combine(profileDirectory, "Cache", "Cache_Data")
        })
        {
            if (Directory.Exists(candidate))
            {
                cacheDirectory = candidate;
                return true;
            }
        }

        cacheDirectory = string.Empty;
        return false;
    }

    private static IEnumerable<string> EnumerateCacheDataFilePaths(string cacheDirectory)
    {
        foreach (var selector in new[] { "data_1", "data_0", "data_2", "data_3" })
        {
            var candidate = Path.Combine(cacheDirectory, selector);
            if (File.Exists(candidate))
            {
                yield return candidate;
            }
        }
    }

    private static CacheEntryCandidate? TryFindCacheEntry(byte[] bytes, string endpoint)
    {
        if (bytes.Length <= BlockFileHeaderSize + EntryStoreSize)
        {
            return null;
        }

        for (var entryStart = BlockFileHeaderSize; entryStart + EntryStoreSize <= bytes.Length; entryStart += EntryStoreSize)
        {
            var keySize = unchecked((int)ReadUInt32(bytes, entryStart + 32));
            if (keySize <= 0 || keySize > EntryKeyInlineMaxLength)
            {
                continue;
            }

            var key = Encoding.UTF8.GetString(bytes, entryStart + EntryKeyOffset, keySize).TrimEnd('\0');
            if (!key.Contains(endpoint, StringComparison.Ordinal))
            {
                continue;
            }

            return new CacheEntryCandidate(
                key,
                unchecked((int)ReadUInt32(bytes, entryStart + 40)),
                ReadUInt32(bytes, entryStart + 56),
                unchecked((int)ReadUInt32(bytes, entryStart + 44)),
                ReadUInt32(bytes, entryStart + 60));
        }

        return null;
    }

    private static byte[]? TryReadCacheStream(string cacheDirectory, uint cacheAddress, int streamSize)
    {
        if (cacheAddress == 0 || streamSize <= 0)
        {
            return null;
        }

        var decodedAddress = DecodeCacheAddress(cacheAddress);
        if (!decodedAddress.IsInitialized)
        {
            return null;
        }

        if (decodedAddress.FileType == 0)
        {
            var separateFilePath = Path.Combine(cacheDirectory, $"f_{decodedAddress.FileNumber:x6}");
            return File.Exists(separateFilePath)
                ? SharedReadRange(separateFilePath, 0, streamSize)
                : null;
        }

        var blockSize = decodedAddress.FileType switch
        {
            2 => 256,
            3 => 1024,
            4 => 4096,
            _ => 0
        };

        if (blockSize == 0)
        {
            return null;
        }

        var blockFilePath = Path.Combine(cacheDirectory, $"data_{decodedAddress.FileNumber}");
        if (!File.Exists(blockFilePath))
        {
            return null;
        }

        var offset = BlockFileHeaderSize + (long)decodedAddress.BlockNumber * blockSize;
        return SharedReadRange(blockFilePath, offset, streamSize);
    }

    private static string ExtractHttpHeaders(byte[] headersBytes)
    {
        var headerText = Encoding.GetEncoding("ISO-8859-1").GetString(headersBytes);
        var httpStart = headerText.IndexOf("HTTP/1.1", StringComparison.Ordinal);
        return httpStart >= 0 ? headerText[httpStart..] : headerText;
    }

    private static string DecodePayload(byte[] payloadBytes, string headersText)
    {
        var contentEncoding = GetHeaderValue(headersText, "content-encoding");
        if (string.Equals(contentEncoding, "br", StringComparison.OrdinalIgnoreCase))
        {
            using var memoryStream = new MemoryStream(payloadBytes);
            using var brotliStream = new BrotliStream(memoryStream, CompressionMode.Decompress);
            using var reader = new StreamReader(brotliStream, Encoding.UTF8);
            return reader.ReadToEnd();
        }

        if (string.Equals(contentEncoding, "gzip", StringComparison.OrdinalIgnoreCase))
        {
            using var memoryStream = new MemoryStream(payloadBytes);
            using var gzipStream = new GZipStream(memoryStream, CompressionMode.Decompress);
            using var reader = new StreamReader(gzipStream, Encoding.UTF8);
            return reader.ReadToEnd();
        }

        return Encoding.UTF8.GetString(payloadBytes);
    }

    private static string? GetHeaderValue(string headersText, string headerName)
    {
        foreach (var line in headersText.Split('\0', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!line.StartsWith(headerName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var separatorIndex = line.IndexOf(':');
            if (separatorIndex < 0 || separatorIndex == line.Length - 1)
            {
                continue;
            }

            return line[(separatorIndex + 1)..].Trim();
        }

        return null;
    }

    private async Task<byte[]?> TryReadEncryptionKeyAsync(string localStatePath, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(localStatePath);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        if (!document.RootElement.TryGetProperty("os_crypt", out var osCrypt)
            || !osCrypt.TryGetProperty("encrypted_key", out var encryptedKeyElement))
        {
            return null;
        }

        var encryptedKeyBase64 = encryptedKeyElement.GetString();
        if (string.IsNullOrWhiteSpace(encryptedKeyBase64))
        {
            return null;
        }

        var encryptedKey = Convert.FromBase64String(encryptedKeyBase64);
        if (HasDpapiPrefix(encryptedKey))
        {
            var trimmedKey = new byte[encryptedKey.Length - 5];
            Array.Copy(encryptedKey, 5, trimmedKey, 0, trimmedKey.Length);
            encryptedKey = trimmedKey;
        }

        return TryUnprotect(encryptedKey);
    }

    private string? TryDecryptCookieValue(byte[]? encryptedValue, byte[] encryptionKey)
    {
        if (encryptedValue is null || encryptedValue.Length == 0)
        {
            return null;
        }

        if (encryptedValue.Length > 3
            && encryptedValue[0] == (byte)'v'
            && encryptedValue[1] == (byte)'1'
            && (encryptedValue[2] == (byte)'0' || encryptedValue[2] == (byte)'1'))
        {
            return TryDecryptAesCookieValue(encryptedValue, encryptionKey);
        }

        var unprotected = TryUnprotect(encryptedValue);
        return unprotected is null ? null : Encoding.UTF8.GetString(unprotected);
    }

    private static bool HasDpapiPrefix(byte[] bytes)
    {
        return bytes.Length >= 5
            && bytes[0] == (byte)'D'
            && bytes[1] == (byte)'P'
            && bytes[2] == (byte)'A'
            && bytes[3] == (byte)'P'
            && bytes[4] == (byte)'I';
    }

    private string? TryDecryptAesCookieValue(byte[] encryptedValue, byte[] encryptionKey)
    {
        if (encryptedValue.Length <= 3 + 12 + 16)
        {
            return null;
        }

        var nonce = encryptedValue.AsSpan(3, 12).ToArray();
        var cipherAndTag = encryptedValue.AsSpan(15).ToArray();
        var cipherTextLength = cipherAndTag.Length - 16;
        if (cipherTextLength <= 0)
        {
            return null;
        }

        var cipherText = cipherAndTag.AsSpan(0, cipherTextLength).ToArray();
        var tag = cipherAndTag.AsSpan(cipherTextLength, 16).ToArray();
        var plainBytes = new byte[cipherTextLength];

        try
        {
            using var aes = new AesGcm(encryptionKey, 16);
            aes.Decrypt(nonce, cipherText, tag, plainBytes);
            return Encoding.UTF8.GetString(plainBytes);
        }
        catch (CryptographicException exception)
        {
            _logger.LogDebug(exception, "Failed to decrypt Chromium AES cookie.");
            return null;
        }
    }

    private static byte[]? TryUnprotect(byte[] protectedBytes)
    {
        DATA_BLOB input = default;
        DATA_BLOB output = default;

        try
        {
            input.cbData = protectedBytes.Length;
            input.pbData = Marshal.AllocHGlobal(protectedBytes.Length);
            Marshal.Copy(protectedBytes, 0, input.pbData, protectedBytes.Length);

            if (!CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, ref output))
            {
                return null;
            }

            var result = new byte[output.cbData];
            Marshal.Copy(output.pbData, result, 0, output.cbData);
            return result;
        }
        finally
        {
            if (input.pbData != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(input.pbData);
            }

            if (output.pbData != IntPtr.Zero)
            {
                LocalFree(output.pbData);
            }
        }
    }

    private static void TryDeleteTemporaryDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }

    private static CodexRateLimitResetCredits? TryDeserializeResetCredits(string responseText)
    {
        if (string.IsNullOrWhiteSpace(responseText))
        {
            return null;
        }

        var directResult = JsonSerializer.Deserialize<CodexRateLimitResetCredits>(responseText, JsonSerializerOptions);
        if (LooksUsable(directResult))
        {
            return directResult;
        }

        using var document = JsonDocument.Parse(responseText);
        foreach (var key in new[] { "data", "result", "payload", "reset_credits", "rate_limit_reset_credits" })
        {
            if (!document.RootElement.TryGetProperty(key, out var nestedElement))
            {
                continue;
            }

            var nestedResult = nestedElement.Deserialize<CodexRateLimitResetCredits>(JsonSerializerOptions);
            if (LooksUsable(nestedResult))
            {
                return nestedResult;
            }
        }

        return directResult;
    }

    private static bool LooksUsable(CodexRateLimitResetCredits? resetCredits)
    {
        return resetCredits is not null
            && (resetCredits.AvailableCount.HasValue
                || resetCredits.SnakeCaseAvailableCount.HasValue
                || (resetCredits.Credits is not null && resetCredits.Credits.Count > 0));
    }

    private static string Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
        {
            return value ?? string.Empty;
        }

        return value[..maxLength];
    }

    private static byte[] SharedReadAllBytes(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var memoryStream = new MemoryStream();
        stream.CopyTo(memoryStream);
        return memoryStream.ToArray();
    }

    private static byte[] SharedReadRange(string path, long offset, int count)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        stream.Seek(offset, SeekOrigin.Begin);

        var buffer = new byte[count];
        var totalRead = 0;
        while (totalRead < count)
        {
            var bytesRead = stream.Read(buffer, totalRead, count - totalRead);
            if (bytesRead <= 0)
            {
                break;
            }

            totalRead += bytesRead;
        }

        if (totalRead == count)
        {
            return buffer;
        }

        Array.Resize(ref buffer, totalRead);
        return buffer;
    }

    private static uint ReadUInt32(byte[] bytes, int offset)
    {
        return BitConverter.ToUInt32(bytes, offset);
    }

    private static DecodedCacheAddress DecodeCacheAddress(uint cacheAddress)
    {
        return new DecodedCacheAddress(
            (cacheAddress & 0x80000000) != 0,
            (int)((cacheAddress >> 28) & 0x7),
            (int)((cacheAddress >> 16) & 0xff),
            (int)(cacheAddress & 0xffff));
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DATA_BLOB pDataIn,
        IntPtr ppszDataDescr,
        IntPtr pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        int dwFlags,
        ref DATA_BLOB pDataOut);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr hMem);

    [StructLayout(LayoutKind.Sequential)]
    private struct DATA_BLOB
    {
        public int cbData;
        public IntPtr pbData;
    }

    private readonly record struct CacheEntryCandidate(
        string Key,
        int HeadersSize,
        uint HeadersAddress,
        int PayloadSize,
        uint PayloadAddress);

    private readonly record struct DecodedCacheAddress(
        bool IsInitialized,
        int FileType,
        int FileNumber,
        int BlockNumber);
}
