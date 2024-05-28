// -----------------------------------------------------------------------
// <copyright file="BlockType.cs">
// Source: https://github.com/dotnet/runtime/tree/9daa4b41eb9f157e79eaf05e2f7451c9c8f6dbdc/src/libraries/System.IO.Compression/src/System/IO/Compression/
// Original code from Microsoft, modified by Rodion Shlomo Solomonyk
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// -----------------------------------------------------------------------

namespace FastZipEntry.Deflate64
{
    internal enum BlockType
    {
        Uncompressed = 0,
        Static = 1,
        Dynamic = 2
    }
}