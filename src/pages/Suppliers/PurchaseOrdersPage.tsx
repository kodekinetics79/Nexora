import React, { useState } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import {
  Box, Typography, Paper, Button, IconButton,
  Dialog, DialogContent, DialogActions,
  Grid, TextField, MenuItem, Select, FormControl,
  Chip,
  Tooltip,
} from '@mui/material';
import {
  Add as AddIcon, Edit as EditIcon,
  ShoppingBag as POIcon,
  Receipt as InvoiceIcon,
  Delete as DeleteIcon,
} from '@mui/icons-material';
import { DataGrid, type GridColDef } from '@mui/x-data-grid';
import supplierPurchaseHistoryService from '../../api/services/supplierPurchaseHistoryService';
import supplierService from '../../api/services/supplierService';
import currencyService from '../../api/services/currencyService';
import productService from '../../api/services/productService';
import { useAuth } from '../../context/AuthContext';
import { useSnackbar } from 'notistack';
import { useTranslation } from 'react-i18next';
import SearchField from '../../components/common/SearchField';
import * as XLSX from 'xlsx';
import { Upload as UploadIcon, FileDownload as DownloadIcon } from '@mui/icons-material';

const emptyPO: any = {
  orderNumber: '',
  customerId: '',
  currency: 'PKR',
  status: 'Received',
  orderDate: new Date().toISOString().split('T')[0],
  expectedDeliveryDate: new Date().toISOString().split('T')[0],
  notes: '',
  totalAmount: 0,
  items: [{ productId: '', quantity: 1, unitPrice: 0, taxAmount: 0, discount: 0, batchNo: '', expiryDate: '', description: '' }],
};

const PurchaseOrdersPage: React.FC = () => {
  const { userData, businessUnits } = useAuth();
  const queryClient = useQueryClient();
  const { enqueueSnackbar } = useSnackbar();
  const { t } = useTranslation();
  const buid = userData?.businessUnitId || 0;

  const activeBU = (businessUnits || []).find((b: any) => b.id === buid);
  const activeBUName = activeBU ? activeBU.businessUnitName : 'Enterprise Solutions';

  const [isModalOpen, setIsModalOpen] = useState(false);
  const [selectedPO, setSelectedPO] = useState<any | null>(null);
  const [formData, setFormData] = useState(emptyPO);
  const [search, setSearch] = useState('');
  const [selectedInvoicePO, setSelectedInvoicePO] = useState<any>(null);
  const [isInvoiceModalOpen, setIsInvoiceModalOpen] = useState(false);

  const handlePrintInvoice = () => {
    if (!selectedInvoicePO) return;
    const printWindow = window.open('', '_blank');
    if (printWindow) {
      const supp = suppliers.find((s: any) => s.id === selectedInvoicePO.customerId);
      const supplierName = supp ? supp.name : 'Unknown Supplier';

      let subtotalSum = 0;
      let taxSum = 0;
      (selectedInvoicePO.items || []).forEach((item: any) => {
        subtotalSum += (Number(item.quantity || 0) * Number(item.unitPrice || 0));
        taxSum += (Number(item.quantity || 0) * Number(item.unitPrice || 0) * (Number(item.taxAmount || 0) / 100));
      });

      const tableRows = (selectedInvoicePO.items || []).map((item: any, idx: number) => {
        const prod = products.find((p: any) => p.id === item.productId);
        const prodName = prod ? prod.productName : 'N/A';
        const partNo = prod ? (prod.partNo || 'N/A') : 'N/A';
        const lineTotal = (Number(item.quantity || 0) * Number(item.unitPrice || 0) * (1 + (Number(item.taxAmount || 0) / 100)));
        return `
          <tr>
            <td style="border: 1px solid #ddd; padding: 12px; text-align: center;">${idx + 1}</td>
            <td style="border: 1px solid #ddd; padding: 12px;">
              <div style="font-weight: 700; color: #1e293b;">${prodName}</div>
              <div style="font-size: 12px; color: #64748b; margin-top: 4px;">Part No: ${partNo}</div>
            </td>
            <td style="border: 1px solid #ddd; padding: 12px; text-align: right;">${item.quantity}</td>
            <td style="border: 1px solid #ddd; padding: 12px; text-align: right;">${Number(item.unitPrice).toLocaleString(undefined, { minimumFractionDigits: 2 })}</td>
            <td style="border: 1px solid #ddd; padding: 12px; text-align: right;">${item.taxAmount || 0}%</td>
            <td style="border: 1px solid #ddd; padding: 12px; text-align: right; font-weight: 700; color: #0f172a;">${lineTotal.toLocaleString(undefined, { minimumFractionDigits: 2 })}</td>
          </tr>
        `;
      }).join('');

      printWindow.document.write(`
        <html>
          <head>
            <title>Invoice - INV-${selectedInvoicePO.orderNumber}</title>
            <style>
              body { font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif; padding: 40px; color: #1e293b; line-height: 1.5; background: #fff; margin: 0; }
              .invoice-header { display: flex; justify-content: space-between; border-bottom: 2px solid #e2e8f0; padding-bottom: 20px; margin-bottom: 32px; }
              .invoice-title { font-size: 28px; font-weight: 900; color: #0f172a; margin: 0 0 4px 0; letter-spacing: -0.02em; }
              .invoice-meta { font-size: 14px; color: #475569; margin: 2px 0; font-weight: 600; }
              .invoice-grid { display: flex; gap: 32px; margin-bottom: 32px; }
              .invoice-col { flex: 1; }
              .section-caption { font-size: 11px; font-weight: 800; color: #475569; letter-spacing: 0.05em; text-transform: uppercase; margin-bottom: 8px; }
              .info-card { padding: 16px; border: 1px solid #e2e8f0; border-radius: 8px; background: #f8fafc; font-size: 14px; }
              .company-title { font-size: 16px; font-weight: 800; color: #1e293b; margin: 0 0 4px 0; }
              .invoice-table { width: 100%; border-collapse: collapse; margin-bottom: 32px; }
              .invoice-table th { background: #f1f5f9; border: 1px solid #e2e8f0; padding: 12px; text-align: left; font-size: 11px; font-weight: 800; color: #475569; letter-spacing: 0.05em; text-transform: uppercase; }
              .invoice-table td { font-size: 14px; }
              .totals-container { display: flex; justify-content: flex-end; margin-top: 16px; }
              .totals-box { min-width: 320px; padding: 16px; border: 1px solid #e2e8f0; border-radius: 8px; background: #f8fafc; }
              .totals-row { display: flex; justify-content: space-between; margin-bottom: 8px; font-size: 14px; }
              .totals-row.grand-total { margin-top: 12px; border-top: 2px solid #E11D2E; padding-top: 12px; font-size: 16px; font-weight: 900; color: #E11D2E; }
              @media print {
                body { padding: 0; margin: 0; }
                .info-card, .invoice-table th { background: #fff !important; }
                .totals-box { background: #fff !important; }
              }
            </style>
          </head>
          <body>
            <div class="invoice-header">
              <div>
                <h1 class="invoice-title">${t('supplier_invoice')}</h1>
                <div class="invoice-meta">${t('invoice_no')} INV-${selectedInvoicePO.orderNumber}</div>
                <div class="invoice-meta">${t('date')}: ${new Date().toLocaleDateString()}</div>
              </div>
              <div style="text-align: right;">
                <h2 style="font-size: 20px; font-weight: 800; color: #E11D2E; margin: 0 0 4px 0;">${activeBUName}</h2>
              </div>
            </div>

            <div class="invoice-grid">
              <div class="invoice-col">
                <div class="section-caption">${t('bill_from')}:</div>
                <div class="info-card">
                  <div class="company-title">${supplierName}</div>
                </div>
              </div>
              <div class="invoice-col">
                <div class="section-caption">${t('bill_to')}:</div>
                <div class="info-card">
                  <div class="company-title">${activeBUName}</div>
                  <div style="color: #64748b;">PO Ref: ${selectedInvoicePO.orderNumber}</div>
                </div>
              </div>
            </div>

            <div class="section-caption">${t('invoice_items')}:</div>
            <table class="invoice-table">
              <thead>
                <tr>
                  <th style="width: 50px; text-align: center;">#</th>
                  <th>${t('product')}</th>
                  <th style="width: 80px; text-align: right;">${t('quantity')}</th>
                  <th style="width: 120px; text-align: right;">${t('price')}</th>
                  <th style="width: 80px; text-align: right;">${t('tax_percent')}</th>
                  <th style="width: 130px; text-align: right;">${t('total')}</th>
                </tr>
              </thead>
              <tbody>
                ${tableRows}
              </tbody>
            </table>

            <div class="totals-container">
              <div class="totals-box">
                <div class="totals-row">
                  <span style="color: #475569; font-weight: 600;">${t('grand_total')}:</span>
                  <span style="color: #1e293b; font-weight: 700;">${subtotalSum.toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 })} ${selectedInvoicePO.currency || 'PKR'}</span>
                </div>
                <div class="totals-row">
                  <span style="color: #475569; font-weight: 600;">${t('tax_amount')}:</span>
                  <span style="color: #1e293b; font-weight: 700;">${taxSum.toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 })} ${selectedInvoicePO.currency || 'PKR'}</span>
                </div>
                <div class="totals-row grand-total">
                  <span>${t('total_incl_tax')}:</span>
                  <span>${Number(selectedInvoicePO.totalAmount || 0).toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 })} ${selectedInvoicePO.currency || 'PKR'}</span>
                </div>
              </div>
            </div>
          </body>
        </html>
      `);
      printWindow.document.close();
      printWindow.focus();
      setTimeout(() => {
        printWindow.print();
        printWindow.close();
      }, 300);
    }
  };

  // ── Queries ──
  const { data: orders = [], isLoading } = useQuery({
    queryKey: ['orders', buid],
    queryFn: async () => {
      const history = await supplierPurchaseHistoryService.getAll(buid);
      const grouped: Record<string, any> = {};
      history.forEach((item: any) => {
        const poId = item.poDocId || `TEMP-${item.id}`;
        if (!grouped[poId]) {
          grouped[poId] = {
            id: item.id,
            orderNumber: poId,
            customerName: item.supplierName || 'Unknown Supplier',
            customerId: item.supplierId,
            orderDate: item.purchaseDate,
            totalAmount: 0,
            currency: item.currency || 'USD',
            status: 'Received',
            createdBy: item.createdBy,
            createdOn: item.createdOn,
            items: []
          };
        }
        grouped[poId].totalAmount += item.quantity * item.unitPrice;
        grouped[poId].items.push(item);
      });
      return Object.values(grouped);
    },
    enabled: !!buid,
  });

  const { data: suppliersData } = useQuery({
    queryKey: ['suppliers-list', buid],
    queryFn: () => supplierService.getAll({ businessUnitId: buid, pageSize: 1000 }),
    enabled: !!buid,
  });
  const suppliers = suppliersData?.items ?? [];

  const { data: productsData } = useQuery({
    queryKey: ['products-list', buid],
    queryFn: () => productService.getAll({ businessUnitId: buid, pageSize: 1000 }),
    enabled: !!buid,
  });
  const products = productsData?.items ?? [];

  useQuery({
    queryKey: ['currencies', buid],
    queryFn: () => currencyService.getAll({ businessUnitId: buid, pageSize: 1000 }),
    enabled: !!buid,
  });

  // ── Mutations ──
  const createMutation = useMutation({
    mutationFn: (data: any) => supplierPurchaseHistoryService.createBatch(data),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['orders'] });
      enqueueSnackbar('Purchase order created successfully', { variant: 'success' });
      setIsModalOpen(false);
    },
    onError: (err: any) => enqueueSnackbar(err.message || 'Failed to create PO', { variant: 'error' }),
  });

  const deleteMutation = useMutation({
    mutationFn: (poDocId: string) => supplierPurchaseHistoryService.deleteByPoNumber(poDocId, buid),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['orders'] });
      enqueueSnackbar('Purchase order deleted successfully', { variant: 'success' });
    },
    onError: (err: any) => enqueueSnackbar(err.message || 'Failed to delete PO', { variant: 'error' }),
  });

  // ── Handlers ──
  const handleAddNew = () => {
    setSelectedItem(null);
    setFormData({ ...emptyPO, orderNumber: `PO-${Date.now().toString().slice(-6)}` });
    setIsModalOpen(true);
  };

  const setSelectedItem = (item: any | null) => {
    setSelectedPO(item);
  };

  const handleEdit = (item: any) => {
    setSelectedPO(item);
    setFormData({
      orderNumber: item.orderNumber || '',
      customerId: item.customerId || '',
      currency: item.currency || 'USD',
      orderDate: item.orderDate ? item.orderDate.split('T')[0] : '',
      expectedDeliveryDate: item.expectedDeliveryDate ? item.expectedDeliveryDate.split('T')[0] : (item.orderDate ? item.orderDate.split('T')[0] : ''),
      status: item.status || 'Received',
      termsAndConditions: item.termsAndConditions || '',
      notes: item.notes || '',
      totalAmount: item.totalAmount || 0,
      items: (item.items || []).map((i: any) => ({
        productId: i.productId || '',
        quantity: i.quantity || 0,
        unitPrice: i.unitPrice || 0,
        taxAmount: i.taxAmount || 0,
        discount: i.discount || 0,
        batchNo: i.batchNo || '',
        expiryDate: i.expiryDate ? i.expiryDate.split('T')[0] : '',
        description: i.productName || i.description || ''
      }))
    });
    setIsModalOpen(true);
  };

  const handleSave = () => {
    const validItems = (formData.items || [])
      .filter((i: any) => i.productId)
      .map((i: any) => ({
        productId: Number(i.productId),
        supplierId: Number(formData.customerId),
        purchaseDate: formData.orderDate ? new Date(formData.orderDate).toISOString() : new Date().toISOString(),
        quantity: Number(i.quantity || 0),
        unitPrice: Number(i.unitPrice || 0),
        currency: formData.currency || 'PKR',
        batchNo: i.batchNo || '',
        expiryDate: i.expiryDate || '',
        createdBy: userData?.userName || 'system',
      }));

    if (validItems.length === 0) {
      enqueueSnackbar('Please add at least one item to purchase', { variant: 'warning' });
      return;
    }

    if (!formData.customerId) {
      enqueueSnackbar('Please select a supplier', { variant: 'warning' });
      return;
    }

    const data = {
      items: validItems,
      businessUnitId: buid,
    };
    if (selectedPO) {
      deleteMutation.mutate(selectedPO.orderNumber, {
        onSuccess: () => createMutation.mutate(data),
      });
    } else {
      createMutation.mutate(data);
    }
  };

  const handleFileUpload = async (event: React.ChangeEvent<HTMLInputElement>) => {
    const file = event.target.files?.[0];
    if (!file) return;

    const reader = new FileReader();
    reader.onload = async (e) => {
      try {
        const data = new Uint8Array(e.target?.result as ArrayBuffer);
        const workbook = XLSX.read(data, { type: 'array' });
        const firstSheetName = workbook.SheetNames[0];
        const worksheet = workbook.Sheets[firstSheetName];
        const jsonData: any[] = XLSX.utils.sheet_to_json(worksheet);

        if (jsonData.length === 0) {
          enqueueSnackbar('The selected file is empty', { variant: 'warning' });
          return;
        }

        // Map Excel columns to API expected format
        // Expected columns: "Product ID", "Supplier ID", "Purchase Date", "Quantity", "Unit Price", "Currency", "Batch No", "Expiry Date"
        const validItems = jsonData.map((row: any) => ({
          productId: Number(row['Product ID'] || row['productId']),
          supplierId: Number(row['Supplier ID'] || row['supplierId']),
          purchaseDate: row['Purchase Date'] ? new Date(row['Purchase Date']).toISOString() : new Date().toISOString(),
          quantity: Number(row['Quantity'] || row['quantity'] || 0),
          unitPrice: Number(row['Unit Price'] || row['unitPrice'] || 0),
          currency: row['Currency'] || row['currency'] || 'PKR',
          batchNo: String(row['Batch No'] || row['batchNo'] || ''),
          expiryDate: row['Expiry Date'] ? new Date(row['Expiry Date']).toISOString() : '',
          createdBy: userData?.userName || 'system',
        })).filter(i => i.productId && i.supplierId);

        if (validItems.length === 0) {
          enqueueSnackbar('No valid items found in the file. Ensure "Product ID" and "Supplier ID" columns exist.', { variant: 'error' });
          return;
        }

        await createMutation.mutateAsync({
          items: validItems,
          businessUnitId: buid,
        });
        
        enqueueSnackbar(`Successfully uploaded ${validItems.length} records`, { variant: 'success' });
      } catch (err: any) {
        enqueueSnackbar('Failed to parse Excel file: ' + err.message, { variant: 'error' });
      }
    };
    reader.readAsArrayBuffer(file);
    // Reset input
    event.target.value = '';
  };

  const downloadTemplate = () => {
    const templateData = [
      {
        'Product ID': 1,
        'Supplier ID': 1,
        'Purchase Date': new Date().toISOString().split('T')[0],
        'Quantity': 100,
        'Unit Price': 50.5,
        'Currency': 'USD',
        'Batch No': 'B123',
        'Expiry Date': new Date(Date.now() + 365*24*60*60*1000).toISOString().split('T')[0]
      }
    ];
    const ws = XLSX.utils.json_to_sheet(templateData);
    const wb = XLSX.utils.book_new();
    XLSX.utils.book_append_sheet(wb, ws, "Template");
    XLSX.writeFile(wb, "PO_History_Template.xlsx");
  };

  const handleAddItem = () => {
    setFormData((p: any) => ({
      ...p,
      items: [...(p.items || []), { productId: '', quantity: 1, unitPrice: 0, taxAmount: 0, discount: 0, batchNo: '', expiryDate: '', description: '' }],
    }));
  };

  const handleRemoveItem = (index: number) => {
    setFormData((p: any) => {
      const items = [...(p.items || [])];
      items.splice(index, 1);
      return { ...p, items };
    });
  };

  const handleItemChange = (index: number, field: string, value: any) => {
    setFormData((p: any) => {
      const items = [...(p.items || [])];
      items[index] = { ...items[index], [field]: value };
      
      // If productId changes, auto-fill unitPrice from the product landed cost
      if (field === 'productId') {
        const product = products.find((prod: any) => prod.id === value);
        if (product) {
          items[index].unitPrice = product.finalLandedCost || 0;
          items[index].description = product.productName;
        }
      }
      return { ...p, items };
    });
  };

  const f = (field: string) => (e: any) => setFormData((p: any) => ({ ...p, [field]: e.target.value }));

  // ── Grid Columns ──
  const columns: GridColDef[] = [
    { 
      field: 'orderNumber', 
      headerName: 'PO Number', 
      width: 150,
      renderCell: (p) => <Typography variant="subtitle2" sx={{ fontWeight: 800, color: 'primary.main', display: 'inline' }}>{p.value}</Typography>
    },
    { field: 'customerName', headerName: 'Supplier', flex: 1 }, // Mapping customerName to Supplier for this view
    { 
      field: 'itemCount', 
      headerName: 'No of Items', 
      width: 120,
      valueGetter: (_v, row) => row.items?.length || 0,
      renderCell: (p) => <Typography variant="body2" sx={{ fontWeight: 700, display: 'inline' }}>{p.row.items?.length || 0}</Typography>
    },
    { 
      field: 'orderDate', 
      headerName: 'Received Date', 
      width: 140, 
      valueFormatter: (v) => v ? new Date(v).toLocaleDateString() : ''
    },
    { 
      field: 'totalAmount', 
      headerName: 'Total Amount', 
      width: 150,
      renderCell: (p: any) => {
        const amt = Number(p.value || 0);
        return `$${amt.toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 })}`;
      }
    },
    { 
      field: 'status', 
      headerName: 'Status', 
      width: 130,
      renderCell: (p) => {
        const colors: Record<string, any> = { 'Pending': 'warning', 'Completed': 'success', 'Cancelled': 'error', 'Received': 'success' };
        return <Chip label={p.value} color={colors[p.value] || 'default'} size="small" sx={{ fontWeight: 700 }} />;
      }
    },
    {
      field: 'actions',
      headerName: 'Actions',
      width: 120,
      sortable: false,
      renderCell: (p) => (
        <Box sx={{ display: 'flex', alignItems: 'center', gap: 0.5, height: '100%' }}>
          <IconButton size="small" color="primary" onClick={() => handleEdit(p.row)} sx={{ p: 0.5 }}><EditIcon fontSize="small" /></IconButton>
          <IconButton size="small" color="error" onClick={() => deleteMutation.mutate(p.row.orderNumber)} sx={{ p: 0.5 }}><DeleteIcon fontSize="small" /></IconButton>
          <IconButton 
            size="small" 
            color="info" 
            onClick={() => {
              setSelectedInvoicePO(p.row);
              setIsInvoiceModalOpen(true);
            }} 
            sx={{ p: 0.5 }}
          >
            <InvoiceIcon fontSize="small" />
          </IconButton>
        </Box>
      )
    }
  ];

  return (
    <Box sx={{ p: 3 }}>
      {/* Header */}
      <Box sx={{ mb: 3, display: 'flex', justifyContent: 'space-between', alignItems: 'flex-start' }}>
        <Box>
          <Typography variant="h4" sx={{ fontWeight: 800, letterSpacing: '-0.02em', mb: 0.5 }}>{t('purchase_orders')}</Typography>
          <Typography variant="body2" color="text.secondary">Manage and track all purchase orders issued to your suppliers</Typography>
        </Box>
        <Box sx={{ display: 'flex', gap: 1 }}>
          <input
            type="file"
            accept=".xlsx, .xls"
            style={{ display: 'none' }}
            id="po-history-upload"
            onChange={handleFileUpload}
          />
          <Tooltip title="Download Excel Template">
            <Button 
              variant="outlined" 
              startIcon={<DownloadIcon />} 
              onClick={downloadTemplate}
              sx={{ borderRadius: 2, height: 48, textTransform: 'none' }}
            >
              Template
            </Button>
          </Tooltip>
          <label htmlFor="po-history-upload">
            <Button 
              variant="outlined" 
              component="span" 
              startIcon={<UploadIcon />} 
              sx={{ borderRadius: 2, height: 48, textTransform: 'none' }}
            >
              Upload History
            </Button>
          </label>
          <Button variant="contained" startIcon={<AddIcon />} onClick={handleAddNew} sx={{ px: 3, borderRadius: 2, height: 48 }}>
            {t('create_new')}
          </Button>
        </Box>
      </Box>



      {/* Search */}
      <Paper sx={{ p: 1, mb: 2, display: 'flex', alignItems: 'center', borderRadius: 2 }}>
        <SearchField value={search} onChange={setSearch} placeholder="Search by PO number or supplier..." />
      </Paper>

      {/* Grid */}
      <Paper sx={{ width: '100%', borderRadius: 2, overflow: 'hidden', border: '1px solid', borderColor: 'divider' }}>
        <DataGrid
          autoHeight
          rows={orders}
          columns={columns}
          loading={isLoading}
          getRowId={(r) => r.id}
          pageSizeOptions={[10, 25, 50]}
          initialState={{
            pagination: { paginationModel: { pageSize: 10 } },
          }}
          disableRowSelectionOnClick
          sx={{ border: 'none' }}
        />
      </Paper>

      {/* Dialog */}
      <Dialog 
        open={isModalOpen} 
        onClose={() => setIsModalOpen(false)} 
        fullWidth 
        maxWidth="lg"
        sx={{
          '& .MuiDialog-paper': {
            borderRadius: 3,
            boxShadow: '0 8px 32px rgba(0,0,0,0.08)',
            bgcolor: '#F8FAFC',
            m: 2,
            maxWidth: '1200px'
          }
        }}
      >
        <DialogContent sx={{ p: 2 }}>
          {/* Top Info section matching screenshot */}
          <Paper 
            elevation={0} 
            sx={{ 
              p: 2, 
              mb: 2, 
              borderRadius: 3, 
              border: '1.5px solid', 
              borderColor: '#E2E8F0', 
              bgcolor: '#F0F7FF' 
            }}
          >
            {/* Header / Title */}
            <Box sx={{ display: 'flex', alignItems: 'center', gap: 1.5, mb: 1.5 }}>
              <Box 
                sx={{ 
                  width: 38, 
                  height: 38, 
                  bgcolor: '#D0E4FF', 
                  borderRadius: 2, 
                  display: 'flex', 
                  alignItems: 'center', 
                  justifyContent: 'center' 
                }}
              >
                <POIcon color="primary" fontSize="small" />
              </Box>
              <Box>
                <Typography variant="subtitle1" sx={{ fontWeight: 800, color: '#1E293B', lineHeight: 1 }}>
                  Purchase Order Details
                </Typography>
                <Typography variant="caption" sx={{ color: '#64748B', fontWeight: 600 }}>
                  supplierAndOrderInfoForAllItems
                </Typography>
              </Box>
            </Box>

            <Grid container spacing={1.5}>
              {/* Row 1 */}
              <Grid size={{ xs: 12, sm: 4 }}>
                <Typography variant="caption" sx={{ display: 'block', fontWeight: 800, color: '#475569', mb: 0.5, letterSpacing: '0.05em' }}>
                  SUPPLIER *
                </Typography>
                <FormControl fullWidth size="small">
                  <Select 
                    value={formData.customerId || ''} 
                    onChange={f('customerId')}
                    sx={{ bgcolor: '#FFFFFF', borderRadius: 2 }}
                  >
                    {suppliers.map((s: any) => (
                      <MenuItem key={s.id} value={s.id} sx={{ fontSize: '0.85rem' }}>{s.name}</MenuItem>
                    ))}
                  </Select>
                </FormControl>
              </Grid>

              <Grid size={{ xs: 12, sm: 4 }}>
                <Typography variant="caption" sx={{ display: 'block', fontWeight: 800, color: '#475569', mb: 0.5, letterSpacing: '0.05em' }}>
                  CURRENCY
                </Typography>
                <FormControl fullWidth size="small">
                  <Select 
                    value={formData.currency || 'PKR'} 
                    onChange={f('currency')}
                    sx={{ bgcolor: '#FFFFFF', borderRadius: 2 }}
                  >
                    <MenuItem value="PKR" sx={{ fontSize: '0.85rem' }}>PKR</MenuItem>
                    <MenuItem value="USD" sx={{ fontSize: '0.85rem' }}>USD</MenuItem>
                    <MenuItem value="EUR" sx={{ fontSize: '0.85rem' }}>EUR</MenuItem>
                  </Select>
                </FormControl>
              </Grid>

              <Grid size={{ xs: 12, sm: 4 }}>
                <Typography variant="caption" sx={{ display: 'block', fontWeight: 800, color: '#475569', mb: 0.5, letterSpacing: '0.05em' }}>
                  STATUS
                </Typography>
                <FormControl fullWidth size="small">
                  <Select 
                    value={formData.status || 'Received'} 
                    onChange={f('status')}
                    sx={{ bgcolor: '#FFFFFF', borderRadius: 2 }}
                  >
                    <MenuItem value="Pending" sx={{ fontSize: '0.85rem' }}>Pending</MenuItem>
                    <MenuItem value="Approved" sx={{ fontSize: '0.85rem' }}>Approved</MenuItem>
                    <MenuItem value="Completed" sx={{ fontSize: '0.85rem' }}>Completed</MenuItem>
                    <MenuItem value="Cancelled" sx={{ fontSize: '0.85rem' }}>Cancelled</MenuItem>
                    <MenuItem value="Received" sx={{ fontSize: '0.85rem' }}>Received</MenuItem>
                  </Select>
                </FormControl>
              </Grid>

              {/* Row 2 */}
              <Grid size={{ xs: 12, sm: 4 }}>
                <Typography variant="caption" sx={{ display: 'block', fontWeight: 800, color: '#475569', mb: 0.5, letterSpacing: '0.05em' }}>
                  ORDER DATE *
                </Typography>
                <TextField 
                  fullWidth 
                  size="small" 
                  type="date" 
                  value={formData.orderDate ? formData.orderDate.split('T')[0] : ''} 
                  onChange={f('orderDate')} 
                  sx={{ 
                    bgcolor: '#FFFFFF', 
                    '& .MuiOutlinedInput-root': { borderRadius: 2, fontSize: '0.85rem' },
                    '& input::-webkit-calendar-picker-indicator': {
                      cursor: 'pointer',
                      opacity: 1,
                      filter: 'invert(0.3) sepia(1) saturate(5) hue-rotate(175deg)',
                      transform: 'scale(1.15)',
                    }
                  }}
                />
              </Grid>

              <Grid size={{ xs: 12, sm: 4 }}>
                <Typography variant="caption" sx={{ display: 'block', fontWeight: 800, color: '#475569', mb: 0.5, letterSpacing: '0.05em' }}>
                  EXPECTED DELIVERY DATE *
                </Typography>
                <TextField 
                  fullWidth 
                  size="small" 
                  type="date" 
                  value={formData.expectedDeliveryDate ? formData.expectedDeliveryDate.split('T')[0] : ''} 
                  onChange={f('expectedDeliveryDate')} 
                  sx={{ 
                    bgcolor: '#FFFFFF', 
                    '& .MuiOutlinedInput-root': { borderRadius: 2, fontSize: '0.85rem' },
                    '& input::-webkit-calendar-picker-indicator': {
                      cursor: 'pointer',
                      opacity: 1,
                      filter: 'invert(0.3) sepia(1) saturate(5) hue-rotate(175deg)',
                      transform: 'scale(1.15)',
                    }
                  }}
                />
              </Grid>

              <Grid size={{ xs: 12, sm: 4 }}>
                <Typography variant="caption" sx={{ display: 'block', fontWeight: 800, color: '#475569', mb: 0.5, letterSpacing: '0.05em' }}>
                  NOTES
                </Typography>
                <TextField 
                  fullWidth 
                  size="small" 
                  placeholder="Additional Notes (optional)" 
                  value={formData.notes || ''} 
                  onChange={f('notes')} 
                  sx={{ bgcolor: '#FFFFFF', '& .MuiOutlinedInput-root': { borderRadius: 2, fontSize: '0.85rem' } }}
                />
              </Grid>
            </Grid>

            {/* Chips beneath */}
            <Box sx={{ mt: 1.5, display: 'flex', gap: 1, flexWrap: 'wrap' }}>
              {(() => {
                const supp = suppliers.find((s: any) => s.id === formData.customerId);
                return supp ? (
                  <Chip 
                    size="small"
                    icon={<AddIcon sx={{ color: '#2E7D32 !important' }} />} 
                    label={supp.name} 
                    sx={{ bgcolor: '#E8F5E9', color: '#2E7D32', fontWeight: 700, borderRadius: 2, border: '1px solid #C8E6C9' }} 
                  />
                ) : null;
              })()}
              <Chip 
                size="small"
                label={formData.currency || 'PKR'} 
                sx={{ bgcolor: '#E3F2FD', color: '#1565C0', fontWeight: 700, borderRadius: 2, border: '1px solid #BBDEFB' }} 
              />
              <Chip 
                size="small"
                label={formData.status || 'Received'} 
                sx={{ bgcolor: '#E0F2F1', color: '#00695C', fontWeight: 700, borderRadius: 2, border: '1px solid #B2DFDB' }} 
              />
            </Box>
          </Paper>

          {/* Items To Purchase Section */}
          <Box sx={{ mt: 2 }}>
            <Typography variant="subtitle2" sx={{ fontWeight: 800, color: '#334155', display: 'flex', alignItems: 'center', gap: 1, mb: 1 }}>
              <POIcon sx={{ color: '#1E293B', fontSize: '1.1rem' }} /> Items To Purchase
            </Typography>

            <Paper 
              elevation={0} 
              sx={{ 
                p: 1.5, 
                borderRadius: 3, 
                border: '1.5px solid', 
                borderColor: '#E2E8F0', 
                bgcolor: '#F8FAFC',
                maxHeight: '260px',
                overflowY: 'auto'
              }}
            >
              {/* Grid table header exactly like screenshot */}
              <Grid container spacing={1} sx={{ mb: 0.5, px: 1, alignItems: 'center' }}>
                <Grid size={{ xs: 12, sm: 0.5 }}>
                  <Typography variant="caption" sx={{ fontWeight: 800, color: '#64748B', fontSize: '0.75rem' }}>#</Typography>
                </Grid>
                <Grid size={{ xs: 12, sm: 3 }}>
                  <Typography variant="caption" sx={{ fontWeight: 800, color: '#64748B', fontSize: '0.75rem' }}>PRODUCT</Typography>
                </Grid>
                <Grid size={{ xs: 12, sm: 1.25 }}>
                  <Typography variant="caption" sx={{ fontWeight: 800, color: '#64748B', fontSize: '0.75rem' }}>QTY</Typography>
                </Grid>
                <Grid size={{ xs: 12, sm: 1.5 }}>
                  <Typography variant="caption" sx={{ fontWeight: 800, color: '#64748B', fontSize: '0.75rem' }}>PRICE</Typography>
                </Grid>
                <Grid size={{ xs: 12, sm: 1.25 }}>
                  <Typography variant="caption" sx={{ fontWeight: 800, color: '#64748B', fontSize: '0.75rem' }}>TAX %</Typography>
                </Grid>
                <Grid size={{ xs: 12, sm: 1.75 }}>
                  <Typography variant="caption" sx={{ fontWeight: 800, color: '#64748B', fontSize: '0.75rem' }}>BATCH</Typography>
                </Grid>
                <Grid size={{ xs: 12, sm: 2.25 }}>
                  <Typography variant="caption" sx={{ fontWeight: 800, color: '#64748B', fontSize: '0.75rem' }}>EXPIRY</Typography>
                </Grid>
                <Grid size={{ xs: 12, sm: 0.5 }}></Grid>
              </Grid>

              {(formData.items || []).map((item: any, index: number) => (
                <Grid container spacing={1} key={index} sx={{ mb: 1, alignItems: 'center', px: 1 }}>
                  <Grid size={{ xs: 12, sm: 0.5 }}>
                    <Typography variant="body2" sx={{ fontWeight: 700, color: '#64748B', fontSize: '0.8rem' }}>
                      {index + 1}
                    </Typography>
                  </Grid>
                  <Grid size={{ xs: 12, sm: 3 }}>
                    <FormControl fullWidth size="small">
                      <Select
                        value={item.productId || ''}
                        onChange={(e) => handleItemChange(index, 'productId', e.target.value)}
                        sx={{ bgcolor: '#FFFFFF', borderRadius: 2, fontSize: '0.8rem' }}
                      >
                        {products.map((p: any) => (
                          <MenuItem key={p.id} value={p.id} sx={{ fontSize: '0.8rem' }}>
                            {p.productName} ({p.partNo || 'N/A'})
                          </MenuItem>
                        ))}
                      </Select>
                    </FormControl>
                  </Grid>
                  <Grid size={{ xs: 12, sm: 1.25 }}>
                    <TextField
                      fullWidth
                      size="small"
                      type="number"
                      value={item.quantity || ''}
                      onChange={(e) => handleItemChange(index, 'quantity', e.target.value)}
                      sx={{ bgcolor: '#FFFFFF', '& .MuiOutlinedInput-root': { borderRadius: 2, fontSize: '0.8rem' } }}
                    />
                  </Grid>
                  <Grid size={{ xs: 12, sm: 1.5 }}>
                    <TextField
                      fullWidth
                      size="small"
                      type="number"
                      value={item.unitPrice || ''}
                      onChange={(e) => handleItemChange(index, 'unitPrice', e.target.value)}
                      sx={{ bgcolor: '#FFFFFF', '& .MuiOutlinedInput-root': { borderRadius: 2, fontSize: '0.8rem' } }}
                    />
                  </Grid>
                  <Grid size={{ xs: 12, sm: 1.25 }}>
                    <TextField
                      fullWidth
                      size="small"
                      type="number"
                      value={item.taxAmount || ''}
                      onChange={(e) => handleItemChange(index, 'taxAmount', e.target.value)}
                      sx={{ bgcolor: '#FFFFFF', '& .MuiOutlinedInput-root': { borderRadius: 2, fontSize: '0.8rem' } }}
                    />
                  </Grid>
                  <Grid size={{ xs: 12, sm: 1.75 }}>
                    <TextField
                      fullWidth
                      size="small"
                      value={item.batchNo || ''}
                      onChange={(e) => handleItemChange(index, 'batchNo', e.target.value)}
                      sx={{ bgcolor: '#FFFFFF', '& .MuiOutlinedInput-root': { borderRadius: 2, fontSize: '0.8rem' } }}
                    />
                  </Grid>
                  <Grid size={{ xs: 12, sm: 2.25 }}>
                    <TextField
                      fullWidth
                      size="small"
                      type="date"
                      value={item.expiryDate ? item.expiryDate.split('T')[0] : ''}
                      onChange={(e) => handleItemChange(index, 'expiryDate', e.target.value)}
                      sx={{ 
                        bgcolor: '#FFFFFF', 
                        '& .MuiOutlinedInput-root': { borderRadius: 2, fontSize: '0.78rem' },
                        '& input::-webkit-calendar-picker-indicator': {
                          cursor: 'pointer',
                          opacity: 1,
                          filter: 'invert(0.3) sepia(1) saturate(5) hue-rotate(175deg)',
                          transform: 'scale(1.15)',
                        }
                      }}
                    />
                  </Grid>
                  <Grid size={{ xs: 12, sm: 0.5 }}>
                    <IconButton size="small" color="error" onClick={() => handleRemoveItem(index)}>
                      <DeleteIcon fontSize="small" />
                    </IconButton>
                  </Grid>
                </Grid>
              ))}

              {/* Table Footer with Items count and Grand Total exactly like screenshot */}
              <Box sx={{ mt: 1.5, display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
                <Box sx={{ display: 'flex', gap: 1.5 }}>
                  <Chip size="small" label={`${formData.items?.length || 0} Items`} sx={{ bgcolor: '#E0F2FE', color: '#0369A1', fontWeight: 700, borderRadius: 2 }} />
                  <Button 
                    startIcon={<AddIcon />} 
                    variant="outlined" 
                    size="small" 
                    onClick={handleAddItem} 
                    sx={{ borderRadius: 2, textTransform: 'none', px: 1.5, fontWeight: 700, fontSize: '0.75rem' }}
                  >
                    Add Item
                  </Button>
                </Box>

                <Box sx={{ display: 'flex', alignItems: 'center', gap: 1 }}>
                  <Typography variant="caption" sx={{ fontWeight: 800, color: '#475569' }}>
                    Grand Total (incl. Tax):
                  </Typography>
                  <Paper 
                    elevation={0} 
                    sx={{ 
                      px: 1.5, 
                      py: 0.5, 
                      bgcolor: '#F0F7FF', 
                      border: '1.5px solid', 
                      borderColor: '#3B82F6', 
                      borderRadius: 2,
                      display: 'flex',
                      alignItems: 'baseline',
                      gap: 0.5
                    }}
                  >
                    <Typography variant="subtitle1" sx={{ color: '#1D4ED8', fontWeight: 900 }}>
                      {(() => {
                        let total = 0;
                        (formData.items || []).forEach((item: any) => {
                          total += (Number(item.quantity || 0) * Number(item.unitPrice || 0) * (1 + (Number(item.taxAmount || 0) / 100)) - Number(item.discount || 0));
                        });
                        return total.toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 });
                      })()}
                    </Typography>
                    <Typography variant="caption" sx={{ color: '#475569', fontWeight: 800 }}>
                      {formData.currency || 'PKR'}
                    </Typography>
                  </Paper>
                </Box>
              </Box>
            </Paper>
          </Box>
        </DialogContent>
        <DialogActions sx={{ px: 2, pb: 2, pt: 0 }}>
          <Button onClick={() => setIsModalOpen(false)} color="inherit" sx={{ fontWeight: 700, textTransform: 'none', fontSize: '0.85rem' }}>Cancel</Button>
          <Button 
            variant="contained" 
            onClick={handleSave} 
            disabled={createMutation.isPending || deleteMutation.isPending}
            sx={{ px: 3, py: 0.5, borderRadius: 2, fontWeight: 700, textTransform: 'none', fontSize: '0.85rem' }}
          >
            {selectedPO ? 'Update PO' : 'Create PO'}
          </Button>
        </DialogActions>
      </Dialog>

      {/* Invoice Modal */}
      <Dialog 
        open={isInvoiceModalOpen} 
        onClose={() => setIsInvoiceModalOpen(false)} 
        fullWidth 
        maxWidth="md"
        sx={{
          '& .MuiDialog-paper': {
            borderRadius: 3,
            boxShadow: '0 12px 40px rgba(0,0,0,0.12)',
            bgcolor: '#FFFFFF',
            m: 2,
            maxWidth: '850px'
          }
        }}
      >
        {selectedInvoicePO && (
          <>
            <DialogContent sx={{ p: 4 }} id="printable-invoice">
              {/* Header section of invoice */}
              <Box sx={{ display: 'flex', justifyContent: 'space-between', alignItems: 'flex-start', mb: 4, borderBottom: '2px solid #F1F5F9', pb: 3 }}>
                <Box>
                  <Typography variant="h5" sx={{ fontWeight: 900, color: '#0F172A', letterSpacing: '-0.02em', mb: 0.5 }}>
                    {t('supplier_invoice')}
                  </Typography>
                  <Typography variant="body2" sx={{ color: '#475569', fontWeight: 600 }}>
                    {t('invoice_no')} INV-{selectedInvoicePO.orderNumber}
                  </Typography>
                  <Typography variant="caption" sx={{ color: '#64748B', display: 'block', mt: 0.5 }}>
                    {t('date')}: {new Date().toLocaleDateString()}
                  </Typography>
                </Box>
                <Box sx={{ textAlign: 'right' }}>
                  <Typography variant="h6" sx={{ fontWeight: 800, color: '#1D4ED8' }}>
                    {activeBUName}
                  </Typography>
                </Box>
              </Box>

              {/* Bill From and Bill To Row */}
              <Grid container spacing={3} sx={{ mb: 4 }}>
                <Grid size={{ xs: 6 }}>
                  <Typography variant="caption" sx={{ display: 'block', fontWeight: 800, color: '#475569', mb: 0.5, letterSpacing: '0.05em' }}>
                    {t('bill_from')}:
                  </Typography>
                  <Paper elevation={0} sx={{ p: 2, bgcolor: '#F8FAFC', borderRadius: 2, border: '1px solid #E2E8F0' }}>
                    <Typography variant="subtitle2" sx={{ fontWeight: 800, color: '#1E293B' }}>
                      {(() => {
                        const supp = suppliers.find((s: any) => s.id === selectedInvoicePO.customerId);
                        return supp ? supp.name : 'Unknown Supplier';
                      })()}
                    </Typography>
                  </Paper>
                </Grid>

                <Grid size={{ xs: 6 }}>
                  <Typography variant="caption" sx={{ display: 'block', fontWeight: 800, color: '#475569', mb: 0.5, letterSpacing: '0.05em' }}>
                    {t('bill_to')}:
                  </Typography>
                  <Paper elevation={0} sx={{ p: 2, bgcolor: '#F8FAFC', borderRadius: 2, border: '1px solid #E2E8F0' }}>
                    <Typography variant="subtitle2" sx={{ fontWeight: 800, color: '#1E293B' }}>
                      {activeBUName}
                    </Typography>
                    <Typography variant="caption" sx={{ color: '#64748B', display: 'block', mt: 0.5 }}>
                      PO Ref: {selectedInvoicePO.orderNumber}
                    </Typography>
                  </Paper>
                </Grid>
              </Grid>

              {/* Items List inside the invoice */}
              <Box sx={{ mb: 4 }}>
                <Typography variant="caption" sx={{ display: 'block', fontWeight: 800, color: '#475569', mb: 1, letterSpacing: '0.05em' }}>
                  {t('invoice_items')}:
                </Typography>
                <Paper elevation={0} sx={{ border: '1px solid #E2E8F0', borderRadius: 2, overflow: 'hidden' }}>
                  {/* Table Headers */}
                  <Grid container spacing={1} sx={{ px: 2, py: 1.25, bgcolor: '#F1F5F9', borderBottom: '1px solid #E2E8F0' }}>
                    <Grid size={{ xs: 1 }}><Typography variant="caption" sx={{ fontWeight: 800, color: '#475569' }}>#</Typography></Grid>
                    <Grid size={{ xs: 5 }}><Typography variant="caption" sx={{ fontWeight: 800, color: '#475569' }}>{t('product')}</Typography></Grid>
                    <Grid size={{ xs: 1.5 }}><Typography variant="caption" sx={{ fontWeight: 800, color: '#475569', textAlign: 'right' }}>{t('quantity')}</Typography></Grid>
                    <Grid size={{ xs: 1.5 }}><Typography variant="caption" sx={{ fontWeight: 800, color: '#475569', textAlign: 'right' }}>{t('price')}</Typography></Grid>
                    <Grid size={{ xs: 1.5 }}><Typography variant="caption" sx={{ fontWeight: 800, color: '#475569', textAlign: 'right' }}>{t('tax_percent')}</Typography></Grid>
                    <Grid size={{ xs: 1.5 }}><Typography variant="caption" sx={{ fontWeight: 800, color: '#475569', textAlign: 'right' }}>{t('total')}</Typography></Grid>
                  </Grid>

                  {/* Dynamic Table Items */}
                  {(selectedInvoicePO.items || []).map((item: any, idx: number) => {
                    const prod = products.find((p: any) => p.id === item.productId);
                    const subtotal = (Number(item.quantity || 0) * Number(item.unitPrice || 0) * (1 + (Number(item.taxAmount || 0) / 100)) - Number(item.discount || 0));
                    return (
                      <Grid container spacing={1} key={idx} sx={{ px: 2, py: 1.25, borderBottom: idx === (selectedInvoicePO.items.length - 1) ? 'none' : '1px solid #E2E8F0', alignItems: 'center' }}>
                        <Grid size={{ xs: 1 }}><Typography variant="body2" sx={{ fontWeight: 700 }}>{idx + 1}</Typography></Grid>
                        <Grid size={{ xs: 5 }}>
                          <Typography variant="body2" sx={{ fontWeight: 700, color: '#1E293B' }}>{prod ? prod.productName : 'N/A'}</Typography>
                          <Typography variant="caption" sx={{ color: '#64748B', display: 'block' }}>Part No: {prod ? (prod.partNo || 'N/A') : 'N/A'}</Typography>
                        </Grid>
                        <Grid size={{ xs: 1.5 }} sx={{ textAlign: 'right' }}><Typography variant="body2" sx={{ fontWeight: 700 }}>{item.quantity}</Typography></Grid>
                        <Grid size={{ xs: 1.5 }} sx={{ textAlign: 'right' }}><Typography variant="body2">{Number(item.unitPrice).toLocaleString(undefined, { minimumFractionDigits: 2 })}</Typography></Grid>
                        <Grid size={{ xs: 1.5 }} sx={{ textAlign: 'right' }}><Typography variant="body2">{item.taxAmount || 0}%</Typography></Grid>
                        <Grid size={{ xs: 1.5 }} sx={{ textAlign: 'right' }}><Typography variant="body2" sx={{ fontWeight: 800, color: '#0F172A' }}>{subtotal.toLocaleString(undefined, { minimumFractionDigits: 2 })}</Typography></Grid>
                      </Grid>
                    );
                  })}
                </Paper>
              </Box>

              {/* Totals Section */}
              <Box sx={{ display: 'flex', justifyContent: 'flex-end', borderTop: '2px solid #F1F5F9', pt: 2.5 }}>
                <Box sx={{ minWidth: 280 }}>
                  <Box sx={{ display: 'flex', justifyContent: 'space-between', mb: 1 }}>
                    <Typography variant="body2" sx={{ color: '#475569', fontWeight: 600 }}>{t('grand_total')}:</Typography>
                    <Typography variant="body2" sx={{ color: '#1E293B', fontWeight: 700 }}>
                      {(() => {
                        let total = 0;
                        (selectedInvoicePO.items || []).forEach((item: any) => {
                          total += (Number(item.quantity || 0) * Number(item.unitPrice || 0));
                        });
                        return total.toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 });
                      })()} {selectedInvoicePO.currency || 'PKR'}
                    </Typography>
                  </Box>
                  <Box sx={{ display: 'flex', justifyContent: 'space-between', mb: 1.5 }}>
                    <Typography variant="body2" sx={{ color: '#475569', fontWeight: 600 }}>{t('tax_amount')}:</Typography>
                    <Typography variant="body2" sx={{ color: '#1E293B', fontWeight: 700 }}>
                      {(() => {
                        let tax = 0;
                        (selectedInvoicePO.items || []).forEach((item: any) => {
                          tax += (Number(item.quantity || 0) * Number(item.unitPrice || 0) * (Number(item.taxAmount || 0) / 100));
                        });
                        return tax.toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 });
                      })()} {selectedInvoicePO.currency || 'PKR'}
                    </Typography>
                  </Box>
                  <Paper elevation={0} sx={{ p: 1.5, bgcolor: '#F0F7FF', border: '1.5px solid', borderColor: '#3B82F6', borderRadius: 2, display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
                    <Typography variant="subtitle2" sx={{ color: '#1D4ED8', fontWeight: 900 }}>{t('total_incl_tax')}:</Typography>
                    <Typography variant="subtitle1" sx={{ color: '#1D4ED8', fontWeight: 900 }}>
                      {Number(selectedInvoicePO.totalAmount || 0).toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 })} {selectedInvoicePO.currency || 'PKR'}
                    </Typography>
                  </Paper>
                </Box>
              </Box>
            </DialogContent>

            <DialogActions sx={{ p: 3, pt: 0, justifyContent: 'space-between' }}>
              <Button onClick={() => setIsInvoiceModalOpen(false)} color="inherit" sx={{ fontWeight: 700, textTransform: 'none' }}>Close</Button>
              <Button 
                variant="contained" 
                color="primary"
                onClick={handlePrintInvoice}
                sx={{ px: 4, py: 1, borderRadius: 2, fontWeight: 700, textTransform: 'none' }}
              >
                Print Invoice
              </Button>
            </DialogActions>
          </>
        )}
      </Dialog>
    </Box>
  );
};

export default PurchaseOrdersPage;
