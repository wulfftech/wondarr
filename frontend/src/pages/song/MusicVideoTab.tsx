import { MusicVideoPanel } from '../../components/MusicVideoModal';
import { Block } from './Facts';

/** The song page's video tab: the manage view while the song holds a video, the candidates while it does not. */
export function MusicVideoTab({ songId, durationMs }: { songId: number; durationMs: number | null }) {
  return (
    <Block title="Music video">
      <MusicVideoPanel songId={songId} songDurationMs={durationMs} />
    </Block>
  );
}
