NVorbis
-------

NVorbis is a .Net library for decoding Xiph.org Vorbis files. It is designed to run in partial trust environments and does not require P/Invoke or unsafe code.

This implementation is based on the Vorbis specification found on xiph.org. The MDCT and Huffman codeword generator were borrowed from public domain implementations in https://github.com/nothings/stb/blob/master/stb_vorbis.c.

Currently the only container supported is Xiph.org Ogg.  Ogg Skeleton and Matroska / WebM are planned (no ETA, though).  RTP support is possible, but not planned.

To use:

```cs
// add a reference to NVorbis.dll

using (var vorbis = new NVorbis.VorbisReader("path/to/file.ogg"))
{
	// get the channels & sample rate
    var channels = vorbis.Channels;
    var sampleRate = vorbis.SampleRate;

    // OPTIONALLY: get a TimeSpan indicating the total length of the Vorbis stream
    var totalTime = vorbis.TotalTime;

	// create a buffer for reading samples
    var readBuffer = new float[channels * sampleRate / 5];	// 200ms

	// get the initial position (obviously the start)
    var position = TimeSpan.Zero;

    // go grab samples
    int cnt;
    while ((cnt = vorbis.ReadSamples(readBuffer, 0, readBuffer.Length)) > 0)
    {
    	// do stuff with the buffer
    
    	// OPTIONALLY: get the position we just read through to...
        position = vorbis.DecodedTime;
    }
}
```

If you are using [NAudio](https://naudio.codeplex.com/), you may try the following:

```cs
// add a reference to NVorbis.dll
// add a reference to NVorbis.NAudioSupport.dll

using (var vorbisStream = new NVorbis.NAudioSupport.VorbisWaveReader("path/to/file.ogg"))
using (var waveOut = new NAudio.Wave.WaveOutEvent())
{
    waveOut.Init(vorbisStream);
    waveOut.Play();
   
    // wait here until playback stops or should stop
}
```
