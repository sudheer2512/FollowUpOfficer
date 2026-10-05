import { compareKinds, highlightSegments, kindMeta, searchTerms } from './search-text';

describe('search text helpers', () => {
  it('extracts distinct terms of two or more characters, longest first', () => {
    expect(searchTerms('  gst  a GST filing ')).toEqual(['filing', 'gst']);
    expect(searchTerms('"eInvoice"')).toEqual(['eInvoice']);
    expect(searchTerms('')).toEqual([]);
    expect(searchTerms(null)).toEqual([]);
  });

  it('splits text into plain and matching segments, case-insensitively', () => {
    expect(highlightSegments('File the GST return', ['gst'])).toEqual([
      { text: 'File the ', match: false },
      { text: 'GST', match: true },
      { text: ' return', match: false },
    ]);
    expect(highlightSegments('gst', ['gst'])).toEqual([{ text: 'gst', match: true }]);
    expect(highlightSegments('No terms', [])).toEqual([{ text: 'No terms', match: false }]);
    expect(highlightSegments(null, ['x'])).toEqual([]);
  });

  it('treats regex characters in the query literally', () => {
    expect(highlightSegments('Cost (Q3) review', ['(Q3)'])).toEqual([
      { text: 'Cost ', match: false },
      { text: '(Q3)', match: true },
      { text: ' review', match: false },
    ]);
  });

  it('orders known kinds canonically and maps unknown kinds to a fallback', () => {
    expect(['Email', 'Zeta', 'Task', 'Alpha', 'Person'].sort(compareKinds)).toEqual(['Task', 'Person', 'Email', 'Alpha', 'Zeta']);
    expect(kindMeta('Teams').plural).toBe('Teams messages');
    expect(kindMeta('Team').plural).toBe('Teams');
    expect(kindMeta('MeetingNote')).toEqual({ icon: 'search', label: 'Meeting Note', plural: 'Meeting Note' });
  });
});
