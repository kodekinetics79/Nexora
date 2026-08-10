import { useState } from "react";
import { useQuery } from "@tanstack/react-query";
import {
  Alert,
  Box,
  Button,
  Chip,
  CircularProgress,
  Paper,
  Stack,
  Table,
  TableBody,
  TableCell,
  TableContainer,
  TableHead,
  TableRow,
  TextField,
  Typography,
} from "@mui/material";
import {
  AssignmentTurnedIn,
  Inventory2,
  OpenInNew,
  Refresh,
  Search,
  ShoppingCartCheckout,
} from "@mui/icons-material";
import { useNavigate } from "react-router-dom";
import procurementService from "../../api/services/procurementService";
import { useAuth } from "../../context/AuthContext";

const money = (value: number, currency: string) =>
  new Intl.NumberFormat(undefined, { style: "currency", currency }).format(
    value,
  );

const readableDate = (value?: string | null) =>
  value ? new Date(value).toLocaleDateString() : "Not scheduled";

export default function PurchaseOrdersPage() {
  const navigate = useNavigate();
  const { hasPermission } = useAuth();
  const canViewRfqs = hasPermission("RFQ Management", "view");
  const canRecordAnswer = hasPermission("Orders", "edit");
  const [searchInput, setSearchInput] = useState("");
  const [search, setSearch] = useState("");
  const ordersQuery = useQuery({
    queryKey: ["supplier-purchase-orders", search],
    queryFn: () => procurementService.getPurchaseOrders(search, 50),
  });

  const submitSearch = () => setSearch(searchInput.trim());
  const orders = ordersQuery.data ?? [];

  return (
    <Box sx={{ p: { xs: 2, md: 3 }, maxWidth: 1280, mx: "auto" }}>
      <Stack spacing={2}>
        <Box>
          <Typography variant="h5" sx={{ fontWeight: 800 }}>
            Supplier purchase orders
          </Typography>
          <Typography color="text.secondary">
            Search authoritative orders by PO, RFQ, supplier, or Nexora Serial
          </Typography>
        </Box>

        {!canViewRfqs && (
          <Alert severity="info">
            Order summaries are available here. Sourcing evidence and the RFQ
            workbench require RFQ Management access.
          </Alert>
        )}

        <Paper variant="outlined" sx={{ p: 2 }}>
          <Stack direction={{ xs: "column", sm: "row" }} spacing={1}>
            <TextField
              fullWidth
              label="Search purchase orders"
              placeholder="PO number, RFQ, supplier, or Nexora Serial"
              value={searchInput}
              onChange={(event) => setSearchInput(event.target.value)}
              onKeyDown={(event) => {
                if (event.key === "Enter") submitSearch();
              }}
            />
            <Button
              variant="contained"
              startIcon={<Search />}
              onClick={submitSearch}
              sx={{ flexShrink: 0 }}
            >
              Search
            </Button>
            <Button
              variant="outlined"
              startIcon={<Refresh />}
              onClick={() => ordersQuery.refetch()}
              sx={{ flexShrink: 0 }}
            >
              Refresh
            </Button>
          </Stack>
        </Paper>

        {ordersQuery.isLoading && (
          <Box sx={{ display: "grid", placeItems: "center", minHeight: 180 }}>
            <CircularProgress aria-label="Loading purchase orders" />
          </Box>
        )}

        {ordersQuery.isError && (
          <Alert
            severity="error"
            action={<Button onClick={() => ordersQuery.refetch()}>Retry</Button>}
          >
            Purchase orders could not be loaded.
          </Alert>
        )}

        {ordersQuery.isSuccess && orders.length === 0 && (
          <Alert severity="info">
            {search
              ? "No purchase orders match this search."
              : "No supplier purchase orders have been created."}
          </Alert>
        )}

        {orders.length > 0 && (
          <TableContainer component={Paper} variant="outlined">
            <Table sx={{ minWidth: 980 }} aria-label="Supplier purchase orders">
              <TableHead>
                <TableRow>
                  <TableCell>Purchase order</TableCell>
                  <TableCell>RFQ / Nexora Serial</TableCell>
                  <TableCell>Supplier</TableCell>
                  <TableCell>Status</TableCell>
                  <TableCell align="right">Value</TableCell>
                  <TableCell align="right">Lines / open qty</TableCell>
                  <TableCell>Expected</TableCell>
                  <TableCell align="right">Action</TableCell>
                </TableRow>
              </TableHead>
              <TableBody>
                {orders.map((order) => (
                  <TableRow key={order.id} hover>
                    <TableCell>
                      <Typography sx={{ fontWeight: 800 }}>
                        {order.purchaseOrderNumber}
                      </Typography>
                      <Typography variant="caption" color="text.secondary">
                        Created {readableDate(order.createdOn)}
                      </Typography>
                    </TableCell>
                    <TableCell>
                      <Typography variant="body2" sx={{ fontWeight: 700 }}>
                        {order.rfqNumber}
                      </Typography>
                      <Typography variant="caption" color="text.secondary">
                        {order.nexoraSerial}
                      </Typography>
                    </TableCell>
                    <TableCell>{order.supplierName}</TableCell>
                    <TableCell>
                      <Chip
                        size="small"
                        label={order.status.replaceAll("_", " ")}
                        color={
                          order.status === "RECEIVED"
                            ? "success"
                            : order.status === "CANCELLED"
                              ? "error"
                              : "warning"
                        }
                      />
                    </TableCell>
                    <TableCell align="right">
                      {money(order.totalValue, order.currencyCode)}
                    </TableCell>
                    <TableCell align="right">
                      {order.lineCount} / {order.openQuantity}
                    </TableCell>
                    <TableCell>{readableDate(order.expectedOn)}</TableCell>
                    <TableCell align="right">
                      {canViewRfqs ? (
                        /*
                          FR-SPO-03. An order sitting at SENT or ISSUED is waiting on the supplier's
                          answer, and that answer is recorded on the order's card in the sourcing
                          workbench. This summary carries no version, so it cannot make the
                          version-checked call itself — it points the buyer at the screen that can,
                          rather than leaving them to guess where an acknowledgement is captured.
                        */
                        <Button
                          size="small"
                          endIcon={
                            canRecordAnswer &&
                            ["SENT", "ISSUED"].includes(order.status) ? (
                              <AssignmentTurnedIn />
                            ) : (
                              <OpenInNew />
                            )
                          }
                          onClick={() =>
                            navigate(`/procurement/rfqs/${order.rfqId}/sourcing`)
                          }
                        >
                          {canRecordAnswer &&
                          ["SENT", "ISSUED"].includes(order.status)
                            ? "Record supplier answer"
                            : "Open workbench"}
                        </Button>
                      ) : (
                        <Typography variant="caption" color="text.secondary">
                          Summary only
                        </Typography>
                      )}
                    </TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          </TableContainer>
        )}

        <Stack direction={{ xs: "column", sm: "row" }} spacing={1}>
          {canViewRfqs && (
            <Button
              variant="contained"
              startIcon={<Inventory2 />}
              onClick={() => navigate("/procurement/rfqs/all")}
            >
              Open procurement RFQs
            </Button>
          )}
          <Button
            variant={canViewRfqs ? "outlined" : "contained"}
            startIcon={<ShoppingCartCheckout />}
            onClick={() => navigate("/sales/orders")}
          >
            Open customer orders
          </Button>
        </Stack>
      </Stack>
    </Box>
  );
}
