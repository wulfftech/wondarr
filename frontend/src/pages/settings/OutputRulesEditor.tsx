import { Fieldset, Select, Stack, Text } from '@mantine/core';
import {
  BITRATE_OPTIONS,
  codecOptions,
  keepRule,
  OUTPUT_RULE_SOURCES,
  SAMPLE_RATE_OPTIONS,
  VBR_QUALITY_OPTIONS,
  type OpusContainerName,
  type OutputCodecName,
  type OutputModeName,
  type OutputPolicy,
  type OutputRule,
} from '../../api/profiles';

/**
 * The conversion rules of one library (LIBRARY_OUTPUT §7.7): one row per source class — YouTube,
 * lossy, lossless — each saying what a file of that class becomes before it is placed. The editor is
 * controlled: it reads the library's `outputPolicy` through `readOutputPolicy` and hands every edit
 * back as the whole version-2 policy, which the library's save writes.
 */

/** The one-line note under the rows, in the order the user asks the three questions. */
const NOTE =
  'A converted file keeps the quality it was downloaded at, so upgrades still look for a better source. ' +
  'A file already in the target format is never re-encoded. Lossless is never made from lossy.';

/** The bitrates the Mode's constant side offers, as the select reads them. */
const BITRATE_CHOICES = BITRATE_OPTIONS.map((kbps) => ({ value: String(kbps), label: `${kbps} kbps` }));

/** The LAME qualities the Mode's VBR side offers, best first. */
const VBR_CHOICES = VBR_QUALITY_OPTIONS.map((quality) => ({ value: String(quality), label: `V${quality}` }));

/** The sample rates the Sample rate field offers besides keeping the source's. */
const SAMPLE_RATE_CHOICES = [
  { value: 'keep', label: 'Keep the source’s' },
  ...SAMPLE_RATE_OPTIONS.map((hz) => ({ value: String(hz), label: `${hz} Hz` })),
];

/** The containers an Opus target — or a kept Opus file — can be named with. */
const CONTAINER_CHOICES: { value: OpusContainerName; label: string }[] = [
  { value: 'opus', label: '.opus' },
  { value: 'ogg', label: '.ogg' },
];

/** Whether the container field means anything for this rule: an Opus target, or a kept YouTube file. */
function usesContainer(source: keyof Omit<OutputPolicy, 'version'>, rule: OutputRule): boolean {
  return rule.codec === 'opus' || (rule.codec === 'keep' && source === 'youtube');
}

/** One source class's rule: the action, and the settings the chosen target is encoded with. */
function RuleRow({
  source,
  label,
  rule,
  onChange,
}: {
  source: keyof Omit<OutputPolicy, 'version'>;
  label: string;
  rule: OutputRule;
  onChange: (rule: OutputRule) => void;
}) {
  const converting = rule.codec !== 'keep';
  const patch = (changes: Partial<OutputRule>) => onChange({ ...rule, ...changes });

  const onCodec = (value: string | null) => {
    const codec = (value ?? 'keep') as OutputCodecName;

    if (codec === 'keep') {
      // A kept file is not re-encoded, so the settings it does not use are written back as "keep" —
      // except the container, which a kept YouTube remux is still renamed by.
      onChange({ ...keepRule(), opusContainer: source === 'youtube' ? rule.opusContainer : 'opus' });

      return;
    }

    patch({
      codec,
      // The LAME quality scale is MP3's alone; every other target is written at a bitrate.
      mode: codec === 'mp3' ? rule.mode : 'cbr',
      bitrateKbps: rule.bitrateKbps === 'keep' ? 256 : rule.bitrateKbps,
      vbrQuality: rule.vbrQuality === 'keep' ? 0 : rule.vbrQuality,
      opusContainer: usesContainer(source, { ...rule, codec }) ? rule.opusContainer : 'opus',
    });
  };

  return (
    <Fieldset legend={label}>
      <Stack gap="xs">
        <Select
          label="Action"
          data={codecOptions(source)}
          value={rule.codec}
          allowDeselect={false}
          onChange={onCodec}
        />

        {converting && (
          <Select
            label="Mode"
            data={[
              { value: 'cbr', label: 'Constant bitrate' },
              ...(rule.codec === 'mp3' ? [{ value: 'vbr' as OutputModeName, label: 'LAME quality (VBR)' }] : []),
            ]}
            value={rule.mode}
            allowDeselect={false}
            onChange={(value) => patch({ mode: value ?? 'cbr' })}
          />
        )}

        {converting && rule.mode === 'cbr' && (
          <Select
            label="Bitrate"
            data={BITRATE_CHOICES}
            value={String(rule.bitrateKbps)}
            allowDeselect={false}
            onChange={(value) => patch({ bitrateKbps: Number(value ?? 256) })}
          />
        )}

        {converting && rule.mode === 'vbr' && (
          <Select
            label="VBR quality"
            data={VBR_CHOICES}
            value={String(rule.vbrQuality)}
            allowDeselect={false}
            onChange={(value) => patch({ vbrQuality: Number(value ?? 0) })}
          />
        )}

        {converting && (
          <Select
            label="Sample rate"
            data={SAMPLE_RATE_CHOICES}
            value={String(rule.sampleRate)}
            allowDeselect={false}
            onChange={(value) => patch({ sampleRate: value === 'keep' || value === null ? 'keep' : Number(value) })}
          />
        )}

        {usesContainer(source, rule) && (
          <Select
            label="Container"
            data={CONTAINER_CHOICES}
            value={rule.opusContainer}
            allowDeselect={false}
            onChange={(value) => patch({ opusContainer: value ?? 'opus' })}
          />
        )}
      </Stack>
    </Fieldset>
  );
}

/** The three conversion rules of one library, edited in place. */
export function OutputRulesEditor({
  policy,
  onChange,
}: {
  policy: OutputPolicy;
  onChange: (policy: OutputPolicy) => void;
}) {
  return (
    <Stack gap="md">
      <Text fw={600}>Conversion rules</Text>

      {OUTPUT_RULE_SOURCES.map((source) => (
        <RuleRow
          key={source.key}
          source={source.key}
          label={source.label}
          rule={policy[source.key]}
          onChange={(rule) => onChange({ ...policy, [source.key]: rule })}
        />
      ))}

      <Text size="xs" c="dimmed">
        {NOTE}
      </Text>
    </Stack>
  );
}
