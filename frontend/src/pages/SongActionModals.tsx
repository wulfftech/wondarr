import { Alert, Button, Group, Modal, SegmentedControl, Select, Stack, Text } from '@mantine/core';
import { CircleAlert } from 'lucide-react';
import { useState } from 'react';
import { useLibraries } from '../api/profiles';
import {
  useCommand,
  useConvert,
  useConvertPreview,
  useMoveSongs,
  type ConvertPlanResource,
  type SongResource,
} from '../api/songs';

/**
 * The two per-song actions the Library page's row menu opens: moving a song to another library, and
 * converting its file. Both run as commands, so the modal shows the command's message.
 */

/** The codecs a one-off conversion rule can name, in the order the picker offers them. */
const CODECS: { value: string; label: string }[] = [
  { value: 'aac', label: 'AAC' },
  { value: 'mp3', label: 'MP3' },
  { value: 'opus', label: 'Opus' },
  { value: 'flac', label: 'FLAC' },
  { value: 'alac', label: 'ALAC' },
];

/** The constant bitrates the picker offers; the range the rule itself accepts is wider. */
const BITRATES = ['128', '192', '256', '320'].map((value) => ({ value, label: `${value} kbps` }));

/** The LAME quality scale; 0 is the best and the slowest. */
const VBR_QUALITIES = ['0', '1', '2', '3', '4', '5'].map((value) => ({ value, label: `V${value}` }));

/** The rule the picker's four values describe, or `null` for "use the library's rules". */
function ruleOf(codec: string, mode: string, bitrate: string, vbrQuality: string): Record<string, unknown> | null {
  if (codec === '') {
    return null;
  }

  if (codec === 'flac' || codec === 'alac') {
    return { codec };
  }

  if (codec === 'mp3' && mode === 'vbr') {
    return { codec, mode: 'vbr', vbrQuality: Number(vbrQuality) };
  }

  return { codec, bitrateKbps: Number(bitrate) };
}

/** A file size as something a person reads: whole megabytes, or kilobytes below one. */
function formatSize(bytes: number | string | null | undefined): string {
  const value = typeof bytes === 'string' ? Number(bytes) : bytes;

  if (value === null || value === undefined || !Number.isFinite(value)) {
    return 'an unknown size';
  }

  const mb = value / (1024 * 1024);

  return mb >= 1 ? `~${Math.round(mb)} MB` : `~${Math.max(1, Math.round(value / 1024))} kB`;
}

/** What one song's plan says: what it would become, or why it would not be touched. */
function planLine(plan: ConvertPlanResource): string {
  const song = plan.songs[0];

  if (song === undefined) {
    return `Nothing to do: ${String(plan.convert)} to convert, ${String(plan.skip)} skipped, ${String(plan.refuse)} refused.`;
  }

  if (song.outcome === 'converted') {
    return `Would convert from ${song.fromCodec ?? '?'} to ${song.toCodec ?? '?'}, ${formatSize(song.estimatedSize)}.`;
  }

  return song.reason ?? 'Nothing to do.';
}

/** Moves one song, and its file, to another library. */
export function MoveSongModal({
  song,
  opened,
  onClose,
}: {
  song: SongResource | null;
  opened: boolean;
  onClose: () => void;
}) {
  const libraries = useLibraries();
  const move = useMoveSongs();
  const [libraryId, setLibraryId] = useState<string | null>(null);
  const [commandId, setCommandId] = useState<number | null>(null);
  const command = useCommand(commandId, { poll: true });

  const currentId = song === null ? null : String(song.libraryId);
  const list = libraries.data ?? [];

  const submit = () => {
    if (song === null || libraryId === null) {
      return;
    }

    move.mutate(
      { songIds: [Number(song.id)], libraryId: Number(libraryId) },
      { onSuccess: (accepted) => setCommandId(Number(accepted.commandId)) },
    );
  };

  return (
    <Modal opened={opened} onClose={onClose} title="Move to library">
      <Stack gap="md">
        <Text size="sm">
          Move “{song?.title ?? ''}” to another library? Its file is moved and re-tagged for the library it lands in.
        </Text>

        <Select
          label="Library"
          placeholder="Pick a library"
          data={list.map((library) => ({
            value: String(library.id),
            label: library.id.toString() === currentId ? `${library.name} (current)` : library.name,
          }))}
          value={libraryId}
          disabled={list.length < 2}
          onChange={setLibraryId}
        />

        {move.error !== null && (
          <Alert color="red" icon={<CircleAlert size={16} />}>
            {move.error.message}
          </Alert>
        )}

        {command.data?.message !== null && command.data?.message !== undefined && (
          <Text size="sm">{command.data.message}</Text>
        )}

        {command.error !== null && (
          <Alert color="red" icon={<CircleAlert size={16} />}>
            {command.error.message}
          </Alert>
        )}

        <Group justify="flex-end">
          <Button variant="default" onClick={onClose}>
            Close
          </Button>
          <Button disabled={libraryId === null} loading={move.isPending} onClick={submit}>
            Move
          </Button>
        </Group>
      </Stack>
    </Modal>
  );
}

/** Converts one song's file in place, by a one-off rule or by its library's own policy. */
export function ConvertSongModal({
  song,
  opened,
  onClose,
}: {
  song: SongResource | null;
  opened: boolean;
  onClose: () => void;
}) {
  const preview = useConvertPreview();
  const convert = useConvert();
  const [codec, setCodec] = useState('');
  const [mode, setMode] = useState('cbr');
  const [bitrate, setBitrate] = useState('320');
  const [vbrQuality, setVbrQuality] = useState('2');
  const [plan, setPlan] = useState<ConvertPlanResource | null>(null);
  const [commandId, setCommandId] = useState<number | null>(null);
  const command = useCommand(commandId, { poll: true });

  const rule = ruleOf(codec, mode, bitrate, vbrQuality);

  const runPreview = () => {
    if (song === null) {
      return;
    }

    preview.mutate({ songIds: [Number(song.id)], rule }, { onSuccess: setPlan });
  };

  const runConvert = () => {
    if (song === null) {
      return;
    }

    convert.mutate(
      { songIds: [Number(song.id)], rule },
      { onSuccess: (accepted) => setCommandId(Number(accepted.commandId)) },
    );
  };

  return (
    <Modal opened={opened} onClose={onClose} title="Convert">
      <Stack gap="md">
        <Text size="sm">
          Convert “{song?.title ?? ''}” in place. The original goes to the recycle bin.
        </Text>

        <Group gap="sm" align="flex-end" wrap="nowrap">
          <Select
            label="Rule"
            placeholder="Use the library's rules"
            data={[{ value: '', label: "Use the library's rules" }, ...CODECS]}
            value={codec}
            w={200}
            onChange={(value) => setCodec(value ?? '')}
          />

          {codec === 'mp3' && (
            <SegmentedControl
              data={[
                { value: 'cbr', label: 'CBR' },
                { value: 'vbr', label: 'VBR' },
              ]}
              value={mode}
              onChange={setMode}
            />
          )}

          {codec !== '' && codec !== 'flac' && codec !== 'alac' && mode === 'cbr' && (
            <Select
              label="Bitrate"
              data={BITRATES}
              value={bitrate}
              allowDeselect={false}
              w={140}
              onChange={(value) => setBitrate(value ?? '320')}
            />
          )}

          {codec === 'mp3' && mode === 'vbr' && (
            <Select
              label="VBR quality"
              data={VBR_QUALITIES}
              value={vbrQuality}
              allowDeselect={false}
              w={120}
              onChange={(value) => setVbrQuality(value ?? '2')}
            />
          )}
        </Group>

        <Group>
          <Button variant="light" loading={preview.isPending} onClick={runPreview}>
            Preview
          </Button>
          <Button loading={convert.isPending} onClick={runConvert}>
            Convert
          </Button>
        </Group>

        {preview.error !== null && (
          <Alert color="red" icon={<CircleAlert size={16} />}>
            {preview.error.message}
          </Alert>
        )}

        {plan !== null && (
          <Text size="sm">{planLine(plan)}</Text>
        )}

        {convert.error !== null && (
          <Alert color="red" icon={<CircleAlert size={16} />}>
            {convert.error.message}
          </Alert>
        )}

        {command.data?.message !== null && command.data?.message !== undefined && (
          <Text size="sm">{command.data.message}</Text>
        )}

        {command.error !== null && (
          <Alert color="red" icon={<CircleAlert size={16} />}>
            {command.error.message}
          </Alert>
        )}

        <Group justify="flex-end">
          <Button variant="default" onClick={onClose}>
            Close
          </Button>
        </Group>
      </Stack>
    </Modal>
  );
}
