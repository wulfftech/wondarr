import { MantineProvider } from '@mantine/core';
import { render, screen, within } from '@testing-library/react';
import userEvent, { type UserEvent } from '@testing-library/user-event';
import { useState } from 'react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { defaultOutputPolicy, readOutputPolicy, writeOutputPolicy, type OutputPolicy } from '../../api/profiles';
import { OutputRulesEditor } from './OutputRulesEditor';

beforeEach(() => {
  // jsdom has no scrollIntoView, which Mantine's combobox calls as its dropdown opens.
  Element.prototype.scrollIntoView = vi.fn();
});

/** The editor with a Save button, so a test reads what the library's save would send. */
function Harness({ initial, onSave }: { initial: OutputPolicy; onSave: (policy: OutputPolicy) => void }) {
  const [policy, setPolicy] = useState(initial);

  return (
    <div>
      <OutputRulesEditor policy={policy} onChange={setPolicy} />
      <button type="button" onClick={() => onSave(writeOutputPolicy(policy))}>
        Save
      </button>
    </div>
  );
}

function renderEditor(initial: OutputPolicy, onSave: (policy: OutputPolicy) => void) {
  return render(
    <MantineProvider>
      <Harness initial={initial} onSave={onSave} />
    </MantineProvider>,
  );
}

/** Opens one row's Select by the label on its input and picks an option of it by label. */
async function pickOption(user: UserEvent, group: RegExp, label: string, option: string): Promise<void> {
  const fieldset = screen.getByRole('group', { name: group });
  const input = within(fieldset).getByLabelText(label, { selector: 'input' });

  await user.click(input);

  const listbox = document.getElementById(input.getAttribute('aria-controls') ?? '');

  if (listbox === null) {
    throw new Error(`the ${label} dropdown of ${group} did not open`);
  }

  await user.click(within(listbox).getByText(option));
}

/** The options one row's Select is showing, read by label rather than by role (jsdom hides them). */
async function openOptions(user: UserEvent, group: RegExp, label: string): Promise<HTMLElement> {
  const fieldset = screen.getByRole('group', { name: group });
  const input = within(fieldset).getByLabelText(label, { selector: 'input' });

  await user.click(input);

  const listbox = document.getElementById(input.getAttribute('aria-controls') ?? '');

  if (listbox === null) {
    throw new Error(`the ${label} dropdown of ${group} did not open`);
  }

  return listbox;
}

describe('OutputRulesEditor', () => {
  it('shows the default policy: YouTube becomes AAC 256, the other two keep their files', () => {
    renderEditor(defaultOutputPolicy(), vi.fn());

    const youtube = screen.getByRole('group', { name: 'YouTube downloads' });

    expect(within(youtube).getByLabelText('Action', { selector: 'input' })).toHaveValue('AAC (.m4a)');
    expect(within(youtube).getByLabelText('Bitrate', { selector: 'input' })).toHaveValue('256 kbps');
    expect(within(youtube).getByLabelText('Mode', { selector: 'input' })).toHaveValue('Constant bitrate');

    for (const name of [/Lossy files/, /Lossless files/]) {
      const row = screen.getByRole('group', { name });

      expect(within(row).getByLabelText('Action', { selector: 'input' })).toHaveValue('Keep as downloaded');
      expect(within(row).queryByLabelText('Bitrate')).toBeNull();
      expect(within(row).queryByLabelText('Mode')).toBeNull();
    }
  });

  it('offers FLAC and ALAC only in the lossless row', async () => {
    const user = userEvent.setup();

    renderEditor(defaultOutputPolicy(), vi.fn());

    const lossy = await openOptions(user, /Lossy files/, 'Action');

    expect(within(lossy).getByText('Keep as downloaded')).toBeInTheDocument();
    expect(within(lossy).getByText('MP3')).toBeInTheDocument();
    expect(within(lossy).queryByText('FLAC')).toBeNull();
    expect(within(lossy).queryByText('ALAC (.m4a)')).toBeNull();

    await user.click(within(lossy).getByText('Keep as downloaded'));

    const lossless = await openOptions(user, /Lossless files/, 'Action');

    expect(within(lossless).getByText('FLAC')).toBeInTheDocument();
    expect(within(lossless).getByText('ALAC (.m4a)')).toBeInTheDocument();
  });

  it('offers the LAME quality scale only for an MP3 target', async () => {
    const user = userEvent.setup();

    renderEditor(defaultOutputPolicy(), vi.fn());

    await pickOption(user, /Lossless files/, 'Action', 'AAC (.m4a)');

    const aac = await openOptions(user, /Lossless files/, 'Mode');

    expect(within(aac).getByText('Constant bitrate')).toBeInTheDocument();
    expect(within(aac).queryByText('LAME quality (VBR)')).toBeNull();

    await user.click(within(aac).getByText('Constant bitrate'));
    await pickOption(user, /Lossless files/, 'Action', 'MP3');

    const mp3 = await openOptions(user, /Lossless files/, 'Mode');

    expect(within(mp3).getByText('LAME quality (VBR)')).toBeInTheDocument();
  });

  it('saves the lossless rule as MP3 VBR V2 and every key of every rule', async () => {
    const user = userEvent.setup();
    const onSave = vi.fn();

    renderEditor(defaultOutputPolicy(), onSave);

    await pickOption(user, /Lossless files/, 'Action', 'MP3');
    await pickOption(user, /Lossless files/, 'Mode', 'LAME quality (VBR)');
    await pickOption(user, /Lossless files/, 'VBR quality', 'V2');
    await user.click(screen.getByRole('button', { name: 'Save' }));

    // The API reads the bitrate and the quality as numbers, so a rule that does not use them
    // carries the defaults a kept file ignores rather than the form's "keep".
    expect(onSave).toHaveBeenCalledWith({
      version: 2,
      youtube: {
        codec: 'aac',
        mode: 'cbr',
        bitrateKbps: 256,
        vbrQuality: 0,
        sampleRate: 'keep',
        opusContainer: 'opus',
      },
      lossy: { codec: 'keep', mode: 'cbr', bitrateKbps: 256, vbrQuality: 0, sampleRate: 'keep', opusContainer: 'opus' },
      lossless: {
        codec: 'mp3',
        mode: 'vbr',
        bitrateKbps: 256,
        vbrQuality: 2,
        sampleRate: 'keep',
        opusContainer: 'opus',
      },
    });
  });

  it('reads a version-1 policy as the YouTube rule with the other two kept', () => {
    renderEditor(
      readOutputPolicy({ version: 1, codec: 'mp3', mode: 'cbr', bitrateKbps: 192, sampleRate: 'keep' }),
      vi.fn(),
    );

    const youtube = screen.getByRole('group', { name: 'YouTube downloads' });

    expect(within(youtube).getByLabelText('Action', { selector: 'input' })).toHaveValue('MP3');
    expect(within(youtube).getByLabelText('Bitrate', { selector: 'input' })).toHaveValue('192 kbps');

    for (const name of [/Lossy files/, /Lossless files/]) {
      expect(within(screen.getByRole('group', { name })).getByLabelText('Action', { selector: 'input' })).toHaveValue(
        'Keep as downloaded',
      );
    }
  });
});
