using System;
using Multiplayer.Networking.Data;

internal static class LoginRequestPolicyTests
{
    public static void ValidEnvelopeAndModsAreAccepted()
    {
        Assert(LoginRequestPolicy.IsValidEnvelope("Player", Guid.NewGuid().ToByteArray(), "", "game|mp3", "Default", 2));
        Assert(LoginRequestPolicy.IsValidMod("mod.id", "1.2.3"));
    }

    public static void NullAndOversizedLoginFieldsAreRejected()
    {
        byte[] guid = Guid.NewGuid().ToByteArray();
        Assert(!LoginRequestPolicy.IsValidEnvelope(null, guid, "", "game|mp3", "Default", 0));
        Assert(!LoginRequestPolicy.IsValidEnvelope("Player", null, "", "game|mp3", "Default", 0));
        Assert(!LoginRequestPolicy.IsValidEnvelope("Player", new byte[15], "", "game|mp3", "Default", 0));
        Assert(!LoginRequestPolicy.IsValidEnvelope("Player", guid, null, "game|mp3", "Default", 0));
        Assert(!LoginRequestPolicy.IsValidEnvelope("Player", guid, "", null, "Default", 0));
        Assert(!LoginRequestPolicy.IsValidEnvelope("Player", guid, "", "game|mp3", null, 0));
        Assert(!LoginRequestPolicy.IsValidEnvelope("Player", guid, "", "game|mp3", "Default", -1));
        Assert(!LoginRequestPolicy.IsValidEnvelope("Player", guid, "", "game|mp3", "Default", LoginRequestPolicy.MaxMods + 1));
        Assert(!LoginRequestPolicy.IsValidMod(null, "1"));
        Assert(!LoginRequestPolicy.IsValidMod("mod", null));
        Assert(!LoginRequestPolicy.IsValidMod(new string('m', LoginRequestPolicy.MaxModIdLength + 1), "1"));
    }

    private static void Assert(bool condition)
    {
        if (!condition) throw new Exception("Assertion failed");
    }
}
