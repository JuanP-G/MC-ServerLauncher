/**
 * When things happen in «Antes de que pite», in seconds. `riff` is where the song's famous synth
 * riff lands; `musicFrom` is where in the song the video starts, so the riff falls on it. Measured
 * with audio/track_map.py on the official video's audio: the riff's long note at 14.03 s, the band
 * coming in at 30.26 s, 116 BPM.
 */
export type SpotTimeline = {
  press: number;
  phone: number;
  riff: number;
  copy: number;
  phone2: number;
  last: number;
  /** One beat of the song, for the punches on the beat while the app is on screen. */
  beat: number;
  ding: number;
  world: number;
  end: number;
  total: number;
  music: string;
  musicFrom: number;
  credit: string;
};

export const SPOT: SpotTimeline = {
  press: 1.5,
  phone: 2.7,
  riff: 5.0,
  copy: 15.4,
  phone2: 16.8,
  last: 19.25,
  beat: 0.5173,
  ding: 21.23,
  world: 22.85,
  end: 26.85,
  total: 30,
  music: "music/final-countdown.wav",
  musicFrom: 9.03,
  credit: "Music: Europe — The Final Countdown",
};
