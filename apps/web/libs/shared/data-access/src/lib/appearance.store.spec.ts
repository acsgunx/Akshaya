import { TestBed } from '@angular/core/testing';

import { AppearanceStore } from './appearance.store';

/**
 * The contract worth testing here is not the signals — it is that they reach
 * `<html>`, because the whole stylesheet keys off those two attributes.
 */
describe('AppearanceStore', () => {
  let root: HTMLElement;

  beforeEach(() => {
    localStorage.clear();
    root = document.documentElement;
    root.removeAttribute('data-theme');
    root.removeAttribute('data-cvd-safe');
    TestBed.configureTestingModule({});
  });

  function store(): AppearanceStore {
    const instance = TestBed.inject(AppearanceStore);
    TestBed.tick(); // flush the DOM-writing effect
    return instance;
  }

  it('defaults to dark, which the stylesheet expresses as no attribute at all', () => {
    expect(store().theme()).toBe('dark');
    expect(root.hasAttribute('data-theme')).toBe(false);
  });

  it('marks the document when the user opts into light', () => {
    const s = store();
    s.toggleTheme();
    TestBed.tick();

    expect(s.theme()).toBe('light');
    expect(root.getAttribute('data-theme')).toBe('light');
  });

  it('marks the document for the colour-blind-safe buy/sell pair', () => {
    const s = store();
    s.setCvdSafe(true);
    TestBed.tick();

    expect(root.getAttribute('data-cvd-safe')).toBe('true');

    s.setCvdSafe(false);
    TestBed.tick();

    expect(root.hasAttribute('data-cvd-safe')).toBe(false);
  });

  it('restores both preferences on the next session', () => {
    const first = store();
    first.toggleTheme();
    first.setCvdSafe(true);
    TestBed.tick();

    TestBed.resetTestingModule();
    TestBed.configureTestingModule({});
    const second = TestBed.inject(AppearanceStore);

    expect(second.theme()).toBe('light');
    expect(second.cvdSafe()).toBe(true);
  });

  it('falls back to the defaults when the stored value is corrupt', () => {
    localStorage.setItem('akshaya.appearance', '{not json');

    const s = store();

    expect(s.theme()).toBe('dark');
    expect(s.cvdSafe()).toBe(false);
  });
});
