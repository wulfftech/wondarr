import { ActionIcon, Tooltip } from '@mantine/core';
import { Play, Square } from 'lucide-react';
import { useCallback, useEffect, useState } from 'react';
import { ApiError } from '../api/errors';
import { usePreviewUrl, type PreviewRequest } from '../api/songs';

/**
 * The audition button the add dialog puts next to every candidate.
 *
 * Every button on the page plays through one shared `Audio` element, so starting a preview stops
 * whichever one was playing (DECISIONS, build session 2 #6: the user auditions candidates one at a
 * time). The URL is asked for at click time and never kept — Deezer's preview links are signed and
 * expire after about half an hour.
 */

/** The one element the whole page plays through. Created on first use so jsdom never needs it. */
const shared = {
  audio: null as HTMLAudioElement | null,
  /** Stops whichever button is playing right now, so a new preview can take the element over. */
  stopCurrent: null as (() => void) | null,
};

function sharedAudio(): HTMLAudioElement {
  shared.audio ??= new Audio();

  return shared.audio;
}

/** Which way in the request takes: a Deezer id when the candidate has one, else its first ISRC. */
function previewRequest(
  deezerId: number | string | null,
  isrcs: string[],
  songId: number | undefined,
): PreviewRequest | null {
  if (songId !== undefined) {
    return { songId };
  }

  if (deezerId !== null && deezerId !== '') {
    return { deezerId: Number(deezerId) };
  }

  const isrc = isrcs.find((value) => value !== '');

  return isrc === undefined ? null : { isrc };
}

export interface PreviewButtonProps {
  /** The Deezer track id, when the candidate came from Deezer or was matched to it. */
  deezerId: number | string | null;
  /** The candidate's ISRCs, used when there is no Deezer id. */
  isrcs?: string[];
  /** A song already in the library; the server resolves its preview from the song itself. */
  songId?: number;
}

export function PreviewButton({ deezerId, isrcs = [], songId }: PreviewButtonProps) {
  const fetchPreview = usePreviewUrl();
  const [playing, setPlaying] = useState(false);
  const [loading, setLoading] = useState(false);
  const [unavailable, setUnavailable] = useState(false);
  const [failed, setFailed] = useState(false);

  const request = previewRequest(deezerId, isrcs, songId);

  const stop = useCallback(() => {
    sharedAudio().pause();
    setPlaying(false);
  }, []);

  useEffect(() => {
    const element = sharedAudio();

    const onEnded = () => {
      setPlaying(false);

      if (shared.stopCurrent === stop) {
        shared.stopCurrent = null;
      }
    };

    element.addEventListener('ended', onEnded);

    return () => {
      element.removeEventListener('ended', onEnded);

      // A button that unmounts mid-preview must not leave the element playing.
      if (shared.stopCurrent === stop) {
        element.pause();
        shared.stopCurrent = null;
      }
    };
  }, [stop]);

  const onClick = () => {
    if (playing) {
      stop();

      if (shared.stopCurrent === stop) {
        shared.stopCurrent = null;
      }

      return;
    }

    if (request === null) {
      return;
    }

    setLoading(true);
    setFailed(false);

    void fetchPreview(request)
      .then((url) => {
        const element = sharedAudio();

        shared.stopCurrent?.();
        element.src = url;
        shared.stopCurrent = stop;
        setPlaying(true);

        // jsdom has no media stack: a test stubs `play`, so its result is not always a promise.
        void Promise.resolve(element.play()).catch(() => {
          setPlaying(false);
          shared.stopCurrent = null;
        });
      })
      .catch((error: unknown) => {
        if (error instanceof ApiError && error.status === 404) {
          setUnavailable(true);
        } else {
          setFailed(true);
        }
      })
      .finally(() => setLoading(false));
  };

  if (unavailable || request === null) {
    return (
      <Tooltip label="No preview">
        <ActionIcon variant="default" aria-label="Preview" disabled>
          <Play size={16} />
        </ActionIcon>
      </Tooltip>
    );
  }

  const label = playing ? 'Stop the preview' : failed ? 'Preview failed — try again' : 'Preview';

  return (
    <Tooltip label={label}>
      <ActionIcon
        variant={playing ? 'filled' : 'default'}
        aria-label="Preview"
        aria-pressed={playing}
        loading={loading}
        onClick={onClick}
      >
        {playing ? <Square size={16} /> : <Play size={16} />}
      </ActionIcon>
    </Tooltip>
  );
}
