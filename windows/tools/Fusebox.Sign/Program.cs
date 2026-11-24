using Fusebox.Core.Update;

// keygen                         -> prints private key (GitHub secret FUSEBOX_WIN_SIGNING_KEY) and public key (Signing.ReleaseKey)
// sign <zip>                     -> writes <zip>.sig; private key from env FUSEBOX_WIN_SIGNING_KEY
// verify <zip> <sig> [pubkey]    -> exit 0 when valid; pubkey defaults to the one built into the app
static int Fail(string m) { Console.Error.WriteLine(m); return 1; }

switch (args.FirstOrDefault())
{
    case "keygen":
        var (priv, pub) = Signing.NewKeyPair();
        Console.WriteLine($"PRIVATE (secret FUSEBOX_WIN_SIGNING_KEY, never commit):\n{priv}\n\nPUBLIC (Signing.ReleaseKey):\n{pub}");
        return 0;
    case "sign" when args.Length == 2:
        var key = Environment.GetEnvironmentVariable("FUSEBOX_WIN_SIGNING_KEY");
        if (string.IsNullOrWhiteSpace(key)) return Fail("FUSEBOX_WIN_SIGNING_KEY is not set");
        using (var f = File.OpenRead(args[1])) File.WriteAllText(args[1] + ".sig", Signing.Sign(f, key));
        Console.WriteLine($"signed {args[1]}");
        return 0;
    case "verify" when args.Length is 3 or 4:
        using (var f = File.OpenRead(args[1]))
        {
            if (!Signing.Verify(f, File.ReadAllText(args[2]), args.Length == 4 ? args[3] : Signing.ReleaseKey)) return Fail("signature INVALID");
        }
        Console.WriteLine("signature ok");
        return 0;
    default:
        return Fail("usage: keygen | sign <zip> | verify <zip> <sig> [pubkey]");
}
