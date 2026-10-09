import { Anchor, Badge, Group, Stack, Text } from '@mantine/core';
import { Link } from 'react-router';
import type { SongDetailsResource } from '../../api/songs';
import { formatCount, plainText, safeUrl } from '../../components/song/format';
import { songPath } from '../../components/song/SongLink';
import { Block, Fact, FactGrid, SourceLink } from './Facts';

type LastFm = NonNullable<SongDetailsResource['lastFm']>;
type Similar = LastFm['similar'][number];

/** How many similar tracks are listed. */
const SIMILAR_LIMIT = 10;

/** One similar track: a song already in the library links to its own page, the rest to Last.fm. */
function SimilarTrack({ track }: { track: Similar }) {
  const label = `${track.artist} – ${track.title}`;
  const href = safeUrl(track.url);
  const match = track.match === null ? null : Math.round(Number(track.match) * 100);

  return (
    <Group gap="xs" wrap="nowrap">
      {track.songId !== null ? (
        <Anchor component={Link} to={songPath(track.songId)} size="sm">
          {label}
        </Anchor>
      ) : href !== null ? (
        <Anchor href={href} target="_blank" rel="noreferrer noopener" size="sm">
          {label}
        </Anchor>
      ) : (
        <Text size="sm">{label}</Text>
      )}
      {track.songId !== null && (
        <Badge size="xs" variant="light" color="green">
          In your library
        </Badge>
      )}
      {match !== null && (
        <Text size="xs" c="dimmed">
          {match}% match
        </Text>
      )}
    </Group>
  );
}

/** Last.fm: listeners, tags, the wiki summary, a short artist bio and similar tracks. Plain text only. */
export function LastFmTab({ lastFm }: { lastFm: LastFm }) {
  const wiki = plainText(lastFm.wiki);
  const bio = plainText(lastFm.artist?.bioSummary);
  const similar = lastFm.similar.slice(0, SIMILAR_LIMIT);

  return (
    <Stack gap="md">
      <Block title="Last.fm">
        <FactGrid>
          <Fact label="Listeners">{formatCount(lastFm.listeners)}</Fact>
          <Fact label="Plays">{formatCount(lastFm.playcount)}</Fact>
        </FactGrid>

        {lastFm.tags.length > 0 && (
          <Group gap={6} aria-label="Last.fm tags">
            {lastFm.tags.map((tag) => (
              <Badge key={tag} variant="light" color="grape" size="sm">
                {tag}
              </Badge>
            ))}
          </Group>
        )}

        {wiki !== '' && (
          <Text size="sm" style={{ whiteSpace: 'pre-line' }}>
            {wiki}
          </Text>
        )}

        <Group>
          <SourceLink url={lastFm.url}>Read more on Last.fm</SourceLink>
        </Group>
      </Block>

      {lastFm.artist !== null && bio !== '' && (
        <Block title={`About ${lastFm.artist.name}`}>
          <Text size="sm" style={{ whiteSpace: 'pre-line' }}>
            {bio}
          </Text>
          <Group>
            <SourceLink url={lastFm.artist.url}>{lastFm.artist.name} on Last.fm</SourceLink>
          </Group>
        </Block>
      )}

      {similar.length > 0 && (
        <Block title="Similar tracks">
          <Stack gap={6}>
            {similar.map((track) => (
              <SimilarTrack key={`${track.artist}|${track.title}`} track={track} />
            ))}
          </Stack>
        </Block>
      )}
    </Stack>
  );
}
