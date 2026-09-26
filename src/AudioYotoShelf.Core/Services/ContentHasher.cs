using System.Security.Cryptography;
using System.Text;

namespace AudioYotoShelf.Core.Services;

/// <summary>
/// Single source of the hash used to key cached icon generations. Both transfer paths (a single
/// book's chapters, and a whole book as one playlist entry) must agree on this, or an icon generated
/// once under one path won't be found as cached under the other for the same prompt.
/// </summary>
public static class ContentHasher
{
    public static string Compute(string input) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(input)));
}
