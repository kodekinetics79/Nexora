import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import PageHeader from './PageHeader';

describe('PageHeader', () => {
  it('exposes the visible page title as the page heading', () => {
    render(<PageHeader title="Platform Users" subtitle="Control-plane accounts." />);

    expect(screen.getByRole('heading', { level: 1, name: 'Platform Users' })).toBeVisible();
  });
});
