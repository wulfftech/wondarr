import { describe, expect, it } from 'vitest';
import { ApiError, countOrThrow } from './errors';

describe('countOrThrow', () => {
  it.each([
    [42, 42],
    ['42', 42],
    [0, 0],
  ])('reads %j as %d', (value, count) => {
    expect(countOrThrow(value, 200, 'no count')).toBe(count);
  });

  it.each([[undefined], [null], ['many'], [Number.NaN], [Number.POSITIVE_INFINITY], [{}]])(
    'refuses %j with the response status',
    (value) => {
      expect(() => countOrThrow(value, 200, 'no count')).toThrow(new ApiError(200, 'no count'));
    },
  );
});
