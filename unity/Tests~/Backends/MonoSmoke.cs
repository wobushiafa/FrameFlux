using System;
using System.Threading;
using System.Threading.Tasks;
using FrameFlux;
using FrameFlux.FFmpeg;
using FrameFlux.WebRtc;
class MonoSmoke
{
    static async Task<int> Main(string[] args)
    {
        try {
            Console.WriteLine("Unity bundled Mono: " + Environment.Version);
            FFmpegHelper.RegisterFFmpeg(args[0]);
            FfmpegWebRtcVideoDecoder.ConfigureNativeLibraryDirectory(args[0]);
            if (!FfmpegWebRtcVideoDecoder.IsSupported) throw new Exception("WebRTC FFmpeg unavailable");
            var video = new Video();
            var audio = new Audio();
            var factory = new FfmpegMediaPlayerFactory(new FfmpegMediaPlayerFactoryOptions { AudioOutputFactory = (rate, channels) => audio });
            var player = factory.Create();
            player.VideoOutput = video;
            player.Error += (s,e) => Console.WriteLine("ERROR " + e.Error.Message);
            await player.OpenAsync(MediaSource.FromFile(args[1]), new MediaOpenOptions {
                Video = new MediaVideoOptions { DecodingPolicy = MediaVideoDecodingPolicy.SoftwareOnly, SnapshotPolicy = MediaSnapshotPolicy.KeepLatestFrame },
                Network = new MediaNetworkOptions { Reconnect = new MediaReconnectOptions { IsEnabled = false } }
            });
            await player.PlayAsync();
            for(int i=0; i<150 && video.Count<3; i++) await Task.Delay(100);
            if(video.Count<3) throw new Exception("No decoded video: " + player.Diagnostics.LastError);
            if(audio.Bytes==0) throw new Exception("No injected audio");
            await player.PauseAsync();
            await player.SeekAsync(TimeSpan.FromSeconds(1));
            player.PlaybackRate = 1.5; player.Volume=0.5; player.IsMuted=true;
            await player.PlayAsync(); await Task.Delay(300);
            var snapshot=await player.CaptureSnapshotAsync();
            if(snapshot==null || snapshot.Data.Length==0) throw new Exception("No snapshot");
            await player.StopAsync(); await player.DisposeAsync(); await player.DisposeAsync();
            Console.WriteLine("PASS video="+video.Count+" audio="+audio.Bytes+" snapshot="+snapshot.Data.Length+" disposed="+audio.Disposed);
            using(var decoder=new FfmpegWebRtcVideoDecoder()) { Console.WriteLine("PASS WebRTC decoder allocation"); }
            await WebRtcLoopback.Run();
            return 0;
        } catch(Exception e) { Console.WriteLine(e); return 1; }
    }
    class Video:IMediaVideoOutput
    {
        public int Count; public MediaFrameStorageKind PreferredFrameStorage => MediaFrameStorageKind.CpuMemory;
        public bool Supports(MediaFrameStorageKind s,MediaPixelFormat p) => s==MediaFrameStorageKind.CpuMemory && p==MediaPixelFormat.Bgra32;
        public bool TryPresent(IMediaFrameLease f) { try { if(!f.TryGetCpuBuffer(out var b) || b.Size<=0) throw new Exception("Invalid frame"); Interlocked.Increment(ref Count); return true; } finally { f.Dispose(); } }
    }
    class Audio:IMediaAudioOutput
    {
        public long Bytes;public bool Disposed;
        public int SampleRate => 48000;public int Channels => 2;public long PlayedFrames => Interlocked.Read(ref Bytes)/4;public bool IsOperational => !Disposed;
        public MediaAudioDiagnostics Diagnostics => MediaAudioDiagnostics.Empty;
        public void Write(ReadOnlySpan<byte> pcm) => Interlocked.Add(ref Bytes,pcm.Length);
        public void Reset() { } public void Dispose() { Disposed=true; }
    }
}
