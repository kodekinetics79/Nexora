/* global URL, Buffer, console, process */
import http from 'node:http';
import contract from './fixture-contract.json' with { type: 'json' };

const apiUrl = process.env.E2E_API_URL || contract.apiUrl;
const baseUrl = process.env.E2E_BASE_URL || contract.baseUrl;
const port = Number(new URL(apiUrl).port);
const now = '2026-07-24T12:00:00Z';

const roles = {
  [contract.manager.email]: { password: contract.manager.password, id: 1, roleId: 1, roleName: 'Release Manager Admin' },
  [contract.editor.email]: { password: contract.editor.password, id: 2, roleId: 2, roleName: 'Release Editor' },
  [contract.denied.email]: { password: contract.denied.password, id: 3, roleId: 3, roleName: 'Restricted Viewer' },
};

const permission = (id, roleId, moduleName, write = false) => ({
  id, roleId, moduleId: id, moduleName, businessUnitId: Number(contract.businessUnitId),
  canCreate: write, canEdit: write, canDelete: false,
});

const permissions = {
  1: ['Dashboard', 'Leads', 'RFQ Management', 'Quotations', 'Orders'].map((name, index) => permission(index + 1, 1, name, true)),
  2: [permission(1, 2, 'Dashboard'), permission(2, 2, 'Leads', true), permission(3, 2, 'RFQ Management', true), permission(4, 2, 'Quotations', true)],
  3: [permission(1, 3, 'Dashboard')],
};

const tokenFor = (role) => {
  const encode = (value) => Buffer.from(JSON.stringify(value)).toString('base64url');
  return `${encode({ alg: 'none', typ: 'JWT' })}.${encode({ sub: String(role.id), role: role.roleName, exp: 4102444800 })}.fixture`;
};

const lead = {
  id: Number(contract.leadId), commercialCaseId: 501, commercialCaseReference: contract.nexoraSerial,
  nexoraSerial: contract.nexoraSerial, customerId: 401, contactId: 402, customerMatchStatus: 'Matched',
  rfqno: 'RFQ-RELEASE-01C', buyersName: contract.customerName, leadSource: 'Fixture upload',
  recDate: now, bidClosingDate: '2026-08-31T00:00:00Z', emailSource: 'fixture@release01c.test',
  clientemail: 'buyer@release01c.test', status: 'Qualified', isAccepted: true, isRejected: false,
  aiconfidence: 0.98, itemCount: 1, reviewVersion: 2, requiresCommercialReview: false,
  commercialFactsVerified: true, opportunityNo: 'OPP-01C', rfqtype: 'Product', createdBy: 'Fixture',
  createdDate: now, businessUnitId: Number(contract.businessUnitId), businessUnitName: 'Release 01C Tenant',
  lifecycleVersion: 2, leadItems: [{ id: 1, lineItemNo: '1', itemMaterialCode: 'NXR-TEST-PART-001', productShortName: 'Fixture part', quantity: 4, unitOfMeasure: 'EA', aiconfidence: 0.99 }],
  attachments: [],
};

const rfq = {
  id: Number(contract.rfqId), commercialCaseId: 501, commercialCaseReference: contract.nexoraSerial,
  nexoraSerial: contract.nexoraSerial, contactId: 402, contactName: 'Release Buyer', rfqno: 'RFQ-RELEASE-01C',
  buyersName: contract.customerName, recDate: now, bidClosingDate: '2026-08-31T00:00:00Z', rfqtype: 'Product',
  leadId: Number(contract.leadId), createdBy: 'Fixture', createdDate: now, businessUnitId: Number(contract.businessUnitId),
  businessUnitName: 'Release 01C Tenant', rfqstatusId: 1, rfqstatusValue: 'Draft', customerId: 401,
  customerName: contract.customerName, customerEmail: 'buyer@release01c.test',
  rfqitems: [{ id: 1, rfqid: Number(contract.rfqId), lineItemNo: '1', productName: 'Fixture part', quantity: 4, unitPrice: 25, bidClosingDateLine: '2026-08-31T00:00:00Z', createdBy: 'Fixture', createdDate: now }],
};

const quote = {
  id: Number(contract.quoteId), quoteNo: 'Q-RELEASE-01C', rfqId: Number(contract.rfqId), rfqNo: rfq.rfqno,
  commercialCaseId: 501, commercialCaseReference: contract.nexoraSerial, nexoraSerial: contract.nexoraSerial,
  lifecycleVersion: 1, version: 1, customerId: 401, contactId: 402, contactName: 'Release Buyer',
  customerName: contract.customerName, businessUnitId: Number(contract.businessUnitId), businessUnitName: 'Release 01C Tenant',
  customerEmail: 'buyer@release01c.test', quoteDate: now, validUntil: '2026-09-30T00:00:00Z', statusId: 1,
  statusValue: 'Draft', statusCode: 'DRAFT', currencyId: 1, currencyCode: 'USD', totalAmount: 100,
  createdBy: 'Fixture', createdDate: now, itemCount: 1,
  quoteItems: [{ id: 1, productId: 1, productName: 'Fixture part', itemDescription: 'NXR-TEST-PART-001', quantity: 4, unitPrice: 25, discount: 0, totalAmount: 100 }],
};

const revisions = [{
  id: 1001, revisionNumber: 2, createdAtUtc: now, fingerprint: 'release01c-fixture-revision-2',
  customerRfqReference: rfq.rfqno, processingPath: 'DeterministicLocal', externalAiUsed: false,
  differences: [{ changeType: 'Modified', scope: 'Line', path: 'items[0].quantity', previousValueJson: '2', currentValueJson: '4' }],
  impacts: [{ aggregateType: 'RFQ', aggregateId: Number(contract.rfqId), impactType: 'QuantityChanged', status: 'ReviewRequired', detailsJson: '{}' }],
}];

const batch = {
  batchId: contract.batchId, filesReceived: 4, logicalInquiries: 4,
  newLeads: 1, exactDuplicates: 1, revisions: 1, possibleMatches: 1, rejected: 0,
  externalOccurrences: 0, externalCost: 0,
  items: [
    { occurrenceId: 1, leadId: Number(contract.leadId), nexoraSerial: contract.nexoraSerial, classification: 'New', revisionNumber: 1, fileName: 'new.csv', ingestedAtUtc: now, processingPath: 'DeterministicLocal', externalAiUsed: false, confidence: 1, reasons: ['New customer RFQ reference'], matchCandidates: [] },
    { occurrenceId: 2, leadId: Number(contract.leadId), nexoraSerial: contract.nexoraSerial, classification: 'ExactDuplicate', revisionNumber: 1, fileName: 'duplicate.csv', ingestedAtUtc: now, processingPath: 'DeterministicLocal', externalAiUsed: false, confidence: 1, reasons: ['Exact content hash'], matchCandidates: [] },
    { occurrenceId: 3, leadId: Number(contract.leadId), nexoraSerial: contract.nexoraSerial, classification: 'Revision', revisionNumber: 2, fileName: 'revision.csv', ingestedAtUtc: now, processingPath: 'DeterministicLocal', externalAiUsed: false, confidence: 0.99, reasons: ['Stable customer RFQ reference with changed quantity'], matchCandidates: [] },
    { occurrenceId: 4, leadId: null, nexoraSerial: null, classification: 'PossibleMatchReviewRequired', revisionNumber: null, fileName: 'possible.csv', ingestedAtUtc: now, processingPath: 'HumanReview', externalAiUsed: false, confidence: 0.72, reasons: ['Similar line fingerprint'], matchCandidates: [{ candidateId: 1, candidateLeadId: Number(contract.leadId), nexoraSerial: contract.nexoraSerial, customerRfqReference: rfq.rfqno, confidence: 0.72, matchEvidenceJson: '{}', differencesJson: '{}', downstreamImpactJson: '{}', reviewState: 'Pending', version: 1 }] },
  ],
};

const dashboard = {
  definitionVersion: 'release-01', generatedAt: now,
  filter: { from: '2026-06-24', to: '2026-07-24', boundary: '[from,to)' },
  roleScope: { scope: 'tenant' },
  kpis: [
    { key: 'leads_received', label: contract.dashboardKpiLabel, value: 2, state: 'available', unit: 'count', numerator: 2, denominator: 2, definition: 'Distinct canonical leads accepted in the selected intake cohort.', insufficientDataReason: null, drillDownIdentifiers: [
      { recordType: 'Lead', recordId: Number(contract.leadId), commercialCaseId: 501, nexoraSerial: contract.nexoraSerial, classification: 'New', occurredAt: now },
      { recordType: 'Lead', recordId: 102, commercialCaseId: 502, nexoraSerial: 'NXR-2026-000102', classification: 'New', occurredAt: now },
    ] },
    { key: 'median_qualification_time', label: 'Median qualification time', value: null, state: 'insufficient_data', unit: 'hours', definition: 'Median elapsed time from accepted intake to qualification.', insufficientDataReason: 'At least three qualified leads are required for this cohort.', drillDownIdentifiers: [] },
  ],
};

const json = (res, status, body) => {
  res.writeHead(status, { 'content-type': 'application/json', 'access-control-allow-origin': baseUrl, 'access-control-allow-credentials': 'true', 'access-control-allow-headers': 'authorization,content-type,idempotency-key', 'access-control-allow-methods': 'GET,POST,PUT,DELETE,OPTIONS' });
  res.end(JSON.stringify(body));
};

const readJson = async (req) => {
  const chunks = [];
  for await (const chunk of req) chunks.push(chunk);
  const raw = Buffer.concat(chunks).toString('utf8');
  try { return raw ? JSON.parse(raw) : {}; } catch { return {}; }
};

const server = http.createServer(async (req, res) => {
  if (req.method === 'OPTIONS') return json(res, 204, {});
  const url = new URL(req.url ?? '/', apiUrl);
  const path = url.pathname.toLowerCase();

  if (req.method === 'GET' && path === '/health') return json(res, 200, { status: 'ready' });
  if (req.method === 'GET' && path === '/api/businessunit/dropdown') return json(res, 200, [{ id: Number(contract.businessUnitId), businessUnitName: 'Release 01C Tenant' }]);
  if (req.method === 'POST' && path === '/api/auth/login') {
    const body = await readJson(req);
    const role = roles[String(body.email ?? '').toLowerCase()];
    if (!role || role.password !== body.password) return json(res, 401, { message: 'Invalid fixture credentials' });
    return json(res, 200, { id: role.id, email: body.email, userName: role.roleName, roleId: role.roleId, roleName: role.roleName, businessUnitId: Number(contract.businessUnitId), businessUnitName: 'Release 01C Tenant', token: tokenFor(role) });
  }
  if (req.method === 'GET' && path === '/api/rolepermission') {
    const roleId = Number(url.searchParams.get('roleId'));
    const items = permissions[roleId] ?? [];
    return json(res, 200, { items, totalCount: items.length, pageNumber: 1, pageSize: 1000 });
  }
  if (req.method === 'GET' && path === '/api/dashboard/release-01') return json(res, 200, dashboard);
  if (req.method === 'GET' && path === `/api/lead/${contract.leadId}`) return json(res, 200, lead);
  if (req.method === 'GET' && path === `/api/leadingestion/leads/${contract.revisionLeadId}/revisions`) return json(res, 200, revisions);
  if (req.method === 'GET' && path === `/api/rfq/${contract.rfqId}`) return json(res, 200, rfq);
  if (req.method === 'GET' && path === `/api/quote/${contract.quoteId}`) return json(res, 200, quote);
  if (req.method === 'GET' && path === `/api/quote/${contract.quoteId}/revisions`) return json(res, 200, { quoteId: Number(contract.quoteId), quoteNo: quote.quoteNo, revisionNo: 1, chainLocked: false, canRevise: false });
  if (req.method === 'GET' && path === `/api/commercial-cases/rfqs/${contract.rfqId}/lifecycle`) return json(res, 200, { aggregateId: Number(contract.rfqId), currentStatusCode: 'DRAFT', version: 1, isTerminal: false, allowedTransitions: [] });
  if (req.method === 'GET' && path === `/api/leadingestion/batches/${contract.batchId}`) return json(res, 200, batch);
  if (req.method === 'POST' && path === '/api/extraction/upload') return json(res, 200, { batchId: contract.batchId, jobs: [{ jobId: 7001, fileName: 'release-01c-inquiry.csv', outcome: 'AlreadyQueued' }] });
  if (req.method === 'GET' && path === '/api/lead') return json(res, 200, { items: [lead], totalCount: 1, pageNumber: 1, pageSize: 25 });
  if (req.method === 'POST' && path === '/api/intelligence/leads/decision-summaries') return json(res, 200, { summaries: {} });
  if (req.method === 'GET' && path === '/api/procurement/purchase-orders') {
    const search = (url.searchParams.get('search') ?? '').toLowerCase();
    const orders = [{
      id: 1301, purchaseOrderNumber: 'PO-SIT-001', rfqId: Number(contract.rfqId),
      rfqNumber: rfq.rfqno, nexoraSerial: contract.nexoraSerial, supplierId: 901,
      supplierName: 'Certified Components Inc.', currencyCode: 'USD', status: 'DRAFT',
      totalValue: 132, expectedOn: '2026-08-15T00:00:00Z', createdOn: now,
      lineCount: 1, openQuantity: 6,
    }].filter(order => !search || JSON.stringify(order).toLowerCase().includes(search));
    return json(res, 200, orders);
  }
  if (req.method === 'POST' && path === '/api/procurement/purchase-orders/1301/issue') {
    const body = await readJson(req);
    if (body.expectedVersion !== 1 || !String(body.deliveryEvidenceReference ?? '').trim()) {
      return json(res, 400, { message: 'Issue evidence and the expected version are required.' });
    }
    return json(res, 200, {
      id: 1301, purchaseOrderNumber: 'PO-SIT-001', status: 'ISSUED', replayed: false,
    });
  }

  return json(res, 404, { message: `Fixture route not implemented: ${req.method} ${url.pathname}` });
});

server.listen(port, '127.0.0.1', () => console.log(`Release 01C fixture API listening on ${apiUrl}`));

const shutdown = () => server.close(() => process.exit(0));
process.on('SIGTERM', shutdown);
process.on('SIGINT', shutdown);
