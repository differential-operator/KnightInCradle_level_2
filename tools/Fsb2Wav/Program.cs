using System;
using System.IO;
using System.Runtime.InteropServices;
using FMOD;
using FmodSystem = FMOD.System;

namespace Fsb2Wav
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            string inDir = args.Length > 0 ? args[0] : @"D:\Documents\GAME\AliceInCradle Win ver029\KnightInCradle\assets\hk\audio";
            string outDir = args.Length > 1 ? args[1] : inDir;
            Directory.CreateDirectory(outDir);

            foreach (string raw in Directory.GetFiles(inDir, "*.raw"))
            {
                try
                {
                    byte[] data = File.ReadAllBytes(raw);
                    byte[] wav = ConvertToWav(data);
                    string outPath = Path.Combine(outDir, Path.GetFileNameWithoutExtension(raw) + ".wav");
                    File.WriteAllBytes(outPath, wav);
                    Console.WriteLine("OK " + Path.GetFileName(raw) + " -> " + wav.Length + " bytes");
                }
                catch (Exception e)
                {
                    Console.WriteLine("FAIL " + Path.GetFileName(raw) + ": " + e);
                }
            }
            Console.WriteLine("完成");
            return 0;
        }

        private static byte[] ConvertToWav(byte[] data)
        {
            FmodSystem system;
            RESULT result = Factory.System_Create(out system);
            if (result != RESULT.OK)
            {
                throw new Exception("System_Create: " + result);
            }
            result = system.init(1, (INITFLAGS)0, IntPtr.Zero);
            if (result != RESULT.OK)
            {
                system.release();
                throw new Exception("init: " + result);
            }

            try
            {
                CREATESOUNDEXINFO exinfo = new CREATESOUNDEXINFO();
                exinfo.cbsize = Marshal.SizeOf(typeof(CREATESOUNDEXINFO));
                exinfo.length = (uint)data.Length;
                Sound sound;
                result = system.createSound(data, (MODE)2048, ref exinfo, out sound);
                if (result != RESULT.OK)
                {
                    throw new Exception("createSound: " + result);
                }

                Sound target = sound;
                int numSub;
                result = sound.getNumSubSounds(out numSub);
                if (result != RESULT.OK)
                {
                    throw new Exception("getNumSubSounds: " + result);
                }
                if (numSub > 0)
                {
                    Sound sub;
                    result = sound.getSubSound(0, out sub);
                    if (result != RESULT.OK)
                    {
                        throw new Exception("getSubSound: " + result);
                    }
                    target = sub;
                }
                byte[] wav = WavFromSound(target);
                if (wav == null)
                {
                    throw new Exception("WavFromSound 返回空");
                }
                return wav;
            }
            finally
            {
                system.release();
            }
        }

        private static byte[] WavFromSound(Sound sound)
        {
            SOUND_TYPE type;
            SOUND_FORMAT format;
            int channels;
            int bits;
            RESULT result = sound.getFormat(out type, out format, out channels, out bits);
            if (result != RESULT.OK)
            {
                return null;
            }

            float frequency;
            int priority;
            result = sound.getDefaults(out frequency, out priority);
            if (result != RESULT.OK)
            {
                return null;
            }
            int freq = (int)frequency;

            uint length;
            result = sound.getLength(out length, TIMEUNIT.PCMBYTES);
            if (result != RESULT.OK)
            {
                return null;
            }

            IntPtr ptr1;
            IntPtr ptr2;
            uint len1;
            uint len2;
            result = sound.@lock(0, length, out ptr1, out ptr2, out len1, out len2);
            if (result != RESULT.OK)
            {
                return null;
            }

            uint total = len1 + len2;
            byte[] wav = new byte[44 + total];
            Buffer.BlockCopy(System.Text.Encoding.ASCII.GetBytes("RIFF"), 0, wav, 0, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(total + 36), 0, wav, 4, 4);
            Buffer.BlockCopy(System.Text.Encoding.ASCII.GetBytes("WAVE"), 0, wav, 8, 4);
            Buffer.BlockCopy(System.Text.Encoding.ASCII.GetBytes("fmt "), 0, wav, 12, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(16), 0, wav, 16, 4);
            Buffer.BlockCopy(BitConverter.GetBytes((short)1), 0, wav, 20, 2);
            Buffer.BlockCopy(BitConverter.GetBytes((short)channels), 0, wav, 22, 2);
            Buffer.BlockCopy(BitConverter.GetBytes(freq), 0, wav, 24, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(freq * channels * bits / 8), 0, wav, 28, 4);
            Buffer.BlockCopy(BitConverter.GetBytes((short)(channels * bits / 8)), 0, wav, 32, 2);
            Buffer.BlockCopy(BitConverter.GetBytes((short)bits), 0, wav, 34, 2);
            Buffer.BlockCopy(System.Text.Encoding.ASCII.GetBytes("data"), 0, wav, 36, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(total), 0, wav, 40, 4);
            Marshal.Copy(ptr1, wav, 44, (int)len1);
            if (len2 > 0)
            {
                Marshal.Copy(ptr2, wav, (int)(44 + len1), (int)len2);
            }
            sound.unlock(ptr1, ptr2, len1, len2);
            return wav;
        }
    }
}
