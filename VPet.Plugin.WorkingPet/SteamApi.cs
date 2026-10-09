using System.Runtime.CompilerServices;
using Steamworks;

namespace VPet.Plugin.WorkingPet;

/// <summary>
/// 对 Steam 云存储 (Facepunch.Steamworks 的 SteamRemoteStorage) 的最薄一层封装.
/// 所有用到 Steamworks 类型的代码都隔离在这里, 并且禁止内联: 如果游戏不是 Steam 版 (没有这个程序集),
/// 只有真正调用到这些方法时才会出错, 并且会被调用方捕获, 不会影响插件的其他功能.
/// 用的是 VPet 自己存档同款的接口 (VPetCloud/Save...), 所以云存储在这个游戏上是启用的.
/// </summary>
internal static class SteamApi
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool IsValid() => SteamClient.IsValid;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static List<string> ListFiles(string prefix) =>
        SteamRemoteStorage.Files.Where(x => x.StartsWith(prefix)).ToList();

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static byte[]? Read(string name) => SteamRemoteStorage.FileRead(name);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool Write(string name, byte[] data) => SteamRemoteStorage.FileWrite(name, data);
}
