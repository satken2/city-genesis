const cache = new Map<string, HTMLAudioElement>();

export function playSound(src: string) {
  let audio = cache.get(src);
  if (!audio) {
    audio = new Audio(src);
    cache.set(src, audio);
  }
  audio.currentTime = 0;
  audio.play();
}
