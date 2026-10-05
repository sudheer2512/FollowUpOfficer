/**
 * Thin signal wrapper over the Web Speech API (SpeechRecognition / webkitSpeechRecognition) for voice delegation.
 * Minimal local types keep `any` out; the browser API is not in every TypeScript DOM lib.
 */
import { DestroyRef, Signal, inject, signal } from '@angular/core';

interface SpeechAlternativeLike {
  readonly transcript: string;
}

interface SpeechResultLike {
  readonly isFinal: boolean;
  readonly length: number;
  readonly [index: number]: SpeechAlternativeLike;
}

interface SpeechResultListLike {
  readonly length: number;
  readonly [index: number]: SpeechResultLike;
}

interface SpeechEventLike {
  readonly resultIndex: number;
  readonly results: SpeechResultListLike;
}

interface SpeechErrorEventLike {
  readonly error: string;
}

interface SpeechRecognitionLike {
  lang: string;
  interimResults: boolean;
  continuous: boolean;
  maxAlternatives: number;
  onresult: ((event: SpeechEventLike) => void) | null;
  onerror: ((event: SpeechErrorEventLike) => void) | null;
  onend: (() => void) | null;
  start(): void;
  stop(): void;
  abort(): void;
}

type SpeechRecognitionCtor = new () => SpeechRecognitionLike;

function recognitionCtor(): SpeechRecognitionCtor | null {
  if (typeof window === 'undefined') return null;
  const w = window as unknown as { SpeechRecognition?: SpeechRecognitionCtor; webkitSpeechRecognition?: SpeechRecognitionCtor };
  return w.SpeechRecognition ?? w.webkitSpeechRecognition ?? null;
}

const ERROR_TEXT: Record<string, string> = {
  'not-allowed': 'Microphone access is blocked — allow it in the browser, or type instead.',
  'service-not-allowed': 'Voice input is not allowed here — type instead.',
  'no-speech': "Didn't catch that — try again, or type instead.",
  'audio-capture': 'No microphone found — type instead.',
  network: 'The speech service is unreachable — type instead.',
  'language-not-supported': 'Indian English speech is not available in this browser — type instead.',
};

export interface SpeechCapture {
  /** False when the browser has no speech recognition (the mic button is disabled). */
  readonly supported: boolean;
  readonly listening: Signal<boolean>;
  /** Words recognised so far in the current utterance (not yet final). */
  readonly interim: Signal<string>;
  readonly error: Signal<string | null>;
  start(): void;
  stop(): void;
  toggle(): void;
}

export interface SpeechHandlers {
  /** A finished phrase. */
  onFinal(text: string): void;
  /** Recognition stopped; `heard` is true when at least one final phrase arrived in this session. */
  onEnd?(heard: boolean): void;
}

/**
 * Creates a speech capture (en-IN, interim results, single utterance). Call in an injection context;
 * recognition is aborted when the owner is destroyed.
 */
export function createSpeechCapture(handlers: SpeechHandlers, lang = 'en-IN'): SpeechCapture {
  const Ctor = recognitionCtor();
  const listening = signal(false);
  const interim = signal('');
  const error = signal<string | null>(null);
  let recognition: SpeechRecognitionLike | null = null;
  let heard = false;

  inject(DestroyRef).onDestroy(() => {
    if (recognition) {
      recognition.onend = null;
      recognition.abort();
      recognition = null;
    }
  });

  function start(): void {
    if (!Ctor || listening()) return;
    error.set(null);
    interim.set('');
    heard = false;
    const r = new Ctor();
    r.lang = lang;
    r.interimResults = true;
    r.continuous = false;
    r.maxAlternatives = 1;
    r.onresult = (event) => {
      let partial = '';
      for (let i = event.resultIndex; i < event.results.length; i++) {
        const result = event.results[i];
        const text = result[0]?.transcript ?? '';
        if (result.isFinal) {
          if (text.trim()) {
            heard = true;
            handlers.onFinal(text.trim());
          }
        } else {
          partial += text;
        }
      }
      interim.set(partial.trim());
    };
    r.onerror = (event) => {
      if (event.error !== 'aborted') error.set(ERROR_TEXT[event.error] ?? 'Voice input stopped — type instead.');
    };
    r.onend = () => {
      listening.set(false);
      interim.set('');
      recognition = null;
      handlers.onEnd?.(heard);
    };
    try {
      r.start();
      recognition = r;
      listening.set(true);
    } catch {
      error.set('Voice input could not start — type instead.');
    }
  }

  function stop(): void {
    recognition?.stop();
  }

  return {
    supported: !!Ctor,
    listening: listening.asReadonly(),
    interim: interim.asReadonly(),
    error: error.asReadonly(),
    start,
    stop,
    toggle: () => (listening() ? stop() : start()),
  };
}
