import { MusicVideoPanel } from '../../components/MusicVideoModal';
import { Block } from './Facts';

/** The song page's video tab: the manage view while the song holds a video, the candidates while it does not. */
export function MusicVideoTab({ songId }: { songId: number }) {
  return (
    <Block title="Music video">
      <MusicVideoPanel songId={songId} />
    </Block>
  );
}
