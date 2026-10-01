import { useEffect, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useSnackbar } from 'notistack';
import productService, { type ProductDTO } from '../../api/services/productService';
import pricingSheetService, { type PriceChange, type PricingRow } from '../../api/services/pricingSheetService';
import useUnsavedWorkGuard from '../../hooks/useUnsavedWorkGuard';

interface Draft { landedCost: string; salePrice: string; currencyId: number | ''; margin?: string }
type Drafts = Record<number, { draft: Draft; original: PricingRow }>;
const number = (value: string) => value.trim() === '' ? null : Number(value);
const change = ({ draft, original }: Drafts[number]): PriceChange => ({
  productId: original.productId, landedCost: number(draft.landedCost),
  salePrice: number(draft.salePrice), currencyId: draft.currencyId === '' ? null : draft.currencyId,
});
const valid = (row: PriceChange) => (
  row.landedCost === null && row.salePrice === null && row.currencyId === null
) || (
  row.landedCost !== null && Number.isFinite(row.landedCost) && row.landedCost > 0
  && row.salePrice !== null && Number.isFinite(row.salePrice) && row.salePrice > 0
  && row.currencyId !== null
);

/** Keeps batch pricing on the product list, using the existing governed pricing endpoint. */
export default function useProductPricing(canEdit: boolean) {
  const client = useQueryClient();
  const { enqueueSnackbar } = useSnackbar();
  const [editing, setEditing] = useState(false);
  const [drafts, setDrafts] = useState<Drafts>({});
  const currencies = useQuery({
    queryKey: ['product-price-currencies'], queryFn: productService.getCurrencies,
    enabled: canEdit && editing,
  });
  // Preserve recoverable work from the former standalone pricing sheet.
  const guard = useUnsavedWorkGuard({
    storageKey: 'nexora.products.pricing-sheet', value: drafts, enabled: canEdit,
    leaveMessage: 'Leave Products without saving your price changes?',
  });
  useEffect(() => {
    if (!guard.recoveredDraft || !canEdit) return;
    setDrafts(guard.recoveredDraft.value);
    setEditing(true);
    guard.acceptRecovered();
  }, [guard.recoveredDraft, guard.acceptRecovered, canEdit]);

  const draftFor = (row: ProductDTO): Draft => drafts[row.id]?.draft ?? {
    landedCost: row.unitCost == null ? '' : String(row.unitCost),
    salePrice: row.sellingPrice == null ? '' : String(row.sellingPrice),
    currencyId: row.priceCurrencyId ?? '',
  };
  const update = (row: ProductDTO, field: keyof Draft, value: string) => {
    const draft = { ...draftFor(row), [field]: field === 'currencyId' ? (value === '' ? '' : Number(value)) : value };
    setDrafts((current) => {
      const original = current[row.id]?.original ?? {
        productId: row.id, partNo: row.partNo, landedCost: row.unitCost,
        salePrice: row.sellingPrice, currencyId: row.priceCurrencyId, onHand: row.qtyOnHand,
      };
      const next = { ...current };
      if (number(draft.landedCost) === (original.landedCost ?? null)
        && number(draft.salePrice) === (original.salePrice ?? null)
        && (draft.currencyId === '' ? null : draft.currencyId) === (original.currencyId ?? null)) {
        delete next[row.id];
      } else next[row.id] = { draft, original };
      return next;
    });
  };
  const changes = Object.values(drafts).map(change);
  const invalid = changes.some((row) => !valid(row));
  const save = useMutation({
    mutationFn: () => {
      if (!canEdit || invalid || changes.length === 0) throw new Error('Complete the price fields before saving.');
      return pricingSheetService.save(changes);
    },
    onSuccess: (result) => {
      setDrafts({}); guard.markSaved({}); setEditing(false);
      for (const key of ['products', 'product-detail', 'pricing-sheet', 'stock-price']) {
        void client.invalidateQueries({ queryKey: [key] });
      }
      enqueueSnackbar(`Saved ${result.saved} ${result.saved === 1 ? 'price' : 'prices'}.`, { variant: 'success' });
    },
  });
  const cancel = () => { setDrafts({}); guard.markSaved({}); setEditing(false); save.reset(); };
  const error = (save.error as { response?: { data?: { detail?: string } } } | null)?.response?.data?.detail
    || 'Prices could not be saved. Your changes are still here; try again.';
  return { editing: canEdit && editing, start: () => setEditing(true), cancel, draftFor, update,
    currencies, save, error, invalid, count: changes.length };
}
