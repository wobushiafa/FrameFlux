using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FrameFlux;
using FrameFlux.WebRtc;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
static class WebRtcLoopback
{
    internal static async Task Run()
    {
        var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();
        int port=((IPEndPoint)listener.LocalEndpoint).Port;
        var server=new RTCPeerConnection(new RTCConfiguration { iceServers=new List<RTCIceServer>() });
        server.addTrack(new MediaStreamTrack(new VideoFormat(VideoCodecsEnum.JPEG,26),MediaStreamStatusEnum.SendOnly));
        server.addTrack(new MediaStreamTrack(new AudioFormat(SDPWellKnownMediaFormatsEnum.PCMU),MediaStreamStatusEnum.SendOnly));
        var connected=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.onconnectionstatechange += state => { Console.WriteLine("Server WebRTC "+state); if(state==RTCPeerConnectionState.connected)connected.TrySetResult(true); };
        var answerTask=Task.Run(async ()=> {
            using(var socket=await listener.AcceptTcpClientAsync())
            using(var stream=socket.GetStream()) {
                var header=new List<byte>();
                while(header.Count<32768) { int b=stream.ReadByte();if(b<0)throw new EndOfStreamException();header.Add((byte)b);int n=header.Count;if(n>=4 && header[n-4]==13 && header[n-3]==10 && header[n-2]==13 && header[n-1]==10)break; }
                string headers=Encoding.ASCII.GetString(header.ToArray());int length=0;
                foreach(var line in headers.Split(new[]{"\r\n"},StringSplitOptions.None))if(line.StartsWith("Content-Length:",StringComparison.OrdinalIgnoreCase))length=int.Parse(line.Substring(15).Trim());
                var body=new byte[length];int received=0;while(received<length){int n=await stream.ReadAsync(body,received,length-received);if(n==0)throw new EndOfStreamException();received+=n;}
                var offer=Encoding.UTF8.GetString(body);
                var result=server.setRemoteDescription(new RTCSessionDescriptionInit {type=RTCSdpType.offer,sdp=offer});if(result!=SetDescriptionResultEnum.OK)throw new Exception("Server offer rejected: "+result);
                var answer=server.createAnswer();await server.setLocalDescription(answer);
                var bytes=Encoding.UTF8.GetBytes(answer.sdp);
                var prefix=Encoding.ASCII.GetBytes("HTTP/1.1 201 Created\r\nContent-Type: application/sdp\r\nContent-Length: "+bytes.Length+"\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(prefix,0,prefix.Length);await stream.WriteAsync(bytes,0,bytes.Length);
            }
        });
        var video=new Output();var audio=new Audio();
        var player=new WebRtcMediaPlayer(new WebRtcPlayerOptions {
            IceServers=new List<RTCIceServer>(),
            HttpClient=new HttpClient(new HttpClientHandler { UseProxy=false }),
            VideoDecoder=new FfmpegWebRtcVideoDecoder(),AudioOutput=audio,SignalingTimeout=TimeSpan.FromSeconds(15)
        });
        player.VideoOutput=video;player.Error+=(s,e)=>Console.WriteLine("WebRTC ERROR "+e.Error.Message);
        try {
            await player.OpenAsync(WebRtcSource.FromWhep("http://127.0.0.1:"+port+"/whep"),new MediaOpenOptions { Video=new MediaVideoOptions {DecodingPolicy=MediaVideoDecodingPolicy.SoftwareOnly}});
            await player.PlayAsync();await answerTask;
            if(await Task.WhenAny(connected.Task,Task.Delay(15000))!=connected.Task)throw new Exception("WebRTC ICE/DTLS timed out");
            var jpeg=Convert.FromBase64String("/9j/4AAQSkZJRgABAgAAAQABAAD//gAQTGF2YzYxLjE5LjEwMQD/2wBDAAgEBAQEBAUFBQUFBQYGBgYGBgYGBgYGBgYHBwcICAgHBwcGBwcGBgcHCAgICAkJCQgICAgJCQoKCgwMCwsODg4RERT/xABMAAEBAAAAAAAAAAAAAAAAAAAABgEBAQAAAAAAAAAAAAAAAAAABgcQAQAAAAAAAAAAAAAAAAAAAAARAQAAAAAAAAAAAAAAAAAAAAD/wAARCAAQABADASIAAhEAAxEA/9oADAMBAAIRAxEAPwCLAFF/f//Z");
            var pcmu=new byte[160];for(int i=0;i<pcmu.Length;i++)pcmu[i]=0x80;
            for(int i=0;i<20;i++){server.SendVideo(9000,jpeg);server.SendAudio(160,pcmu);await Task.Delay(100);}
            if(video.Frames==0 || audio.Samples==0)throw new Exception("WebRTC did not decode media: video="+video.Frames+" audio="+audio.Samples);
            Console.WriteLine("PASS WHEP/ICE/DTLS/SRTP/JPEG/PCMU video="+video.Frames+" audio="+audio.Samples);
            if (player.Capabilities.CanPause) throw new Exception("Incorrect WebRTC pause capability");
            await player.StopAsync();
        } finally {await player.DisposeAsync();await player.DisposeAsync();server.Close("test complete");listener.Stop();}
    }
    class Output:IMediaVideoOutput {
        public int Frames;public MediaFrameStorageKind PreferredFrameStorage=>MediaFrameStorageKind.CpuMemory;
        public bool Supports(MediaFrameStorageKind storage,MediaPixelFormat format)=>storage==MediaFrameStorageKind.CpuMemory && format==MediaPixelFormat.Bgra32;
        public bool TryPresent(IMediaFrameLease frame){try{Interlocked.Increment(ref Frames);return true;}finally{frame.Dispose();}}
    }
    class Audio:IWebRtcAudioOutput {
        public long Samples;public bool IsSupported=>true;
        public void EnsureFormat(int rate,int channels) { }
        public void WriteSamples(ReadOnlySpan<short> samples)=>Interlocked.Add(ref Samples,samples.Length);
        public void SetVolume(double volume,bool muted){} public void Pause(){} public void Resume(){} public void Reset(){} public void Dispose(){}
    }
}
