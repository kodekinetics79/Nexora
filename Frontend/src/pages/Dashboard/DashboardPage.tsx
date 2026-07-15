import { useEffect, useState } from 'react';
import { Box, Typography, Paper, Card, CircularProgress, IconButton, Tooltip, useTheme, Avatar, Grid, Table, TableBody, TableCell, TableContainer, TableHead, TableRow, Chip } from '@mui/material';

import { 
  TrendingUp, 
  TrendingDown, 
  LocalAtm, 
  Assignment, 
  People,
  Assessment,
  Refresh,
  Speed,
  HealthAndSafety,
  ScatterPlot,
  ListAlt
} from '@mui/icons-material';
import { useAppTheme } from '../../context/ThemeContext';
import axiosInstance from '../../api/axiosInstance';
import { 
  AreaChart, Area, XAxis, YAxis, CartesianGrid, Tooltip as RechartsTooltip, ResponsiveContainer, 
  PieChart, Pie, Cell,
  Radar, RadarChart, PolarGrid, PolarAngleAxis, PolarRadiusAxis,
  BarChart, Bar,
  ScatterChart, Scatter, ZAxis
} from 'recharts';
import dayjs from 'dayjs';

export interface StatTrendDTO {
  value: string;
  isUp: boolean;
}

export interface DashboardStatsDTO {
  totalLeads: number;
  activeLeads: number;
  totalRfqs: number;
  rfqsQuoted: number;
  bidRatio: number;
  totalLineItems: number;
  l1Quoted: number;
  totalOrderValue: number;
  winVolumeRatio: number;
  avgQuoteValue: number;
  avgOrderValue: number;
  customerCount: number;
  conversionRates: {
    leadToRfq: number;
    rfqToQuote: number;
    quoteToOrder: number;
  };
  leadsTrend?: StatTrendDTO;
  rfqsTrend?: StatTrendDTO;
  ordersTrend?: StatTrendDTO;
}

export interface MonthlyTrendDTO {
  month: string;
  count: number;
  value: number;
}

export interface CategoryDistributionDTO {
  categoryName: string;
  count: number;
  percentage: number;
}

export interface RadarDataDTO {
  subject: string;
  a: number;
  b: number;
}

export interface ScatterDataDTO {
  x: number;
  y: number;
  z: number;
  name: string;
}

export interface RecentItemDTO {
  id: string;
  type: string;
  description: string;
  status: string;
  date: string;
}

export interface DashboardDataDTO {
  stats: DashboardStatsDTO;
  volumeTrend: MonthlyTrendDTO[];
  statusDistribution: CategoryDistributionDTO[];
  efficiencyVelocity: CategoryDistributionDTO[];
  operationalHealth: RadarDataDTO[];
  responseIntegrity: ScatterDataDTO[];
  recentItems: RecentItemDTO[];
  sourceDistribution: CategoryDistributionDTO[];
}

const StatCard = ({ title, value, icon, trend, trendValue, color, delay }: any) => {
  const { mode } = useAppTheme();
  return (
    <Card sx={{ 
      borderRadius: 4, 
      p: 3, 
      backgroundColor: mode === 'dark' ? 'rgba(30, 41, 59, 0.7)' : 'rgba(255, 255, 255, 0.8)',
      backdropFilter: 'blur(12px)',
      border: '1px solid',
      borderColor: mode === 'dark' ? 'rgba(255, 255, 255, 0.05)' : 'rgba(0, 0, 0, 0.05)',
      boxShadow: mode === 'dark' ? `0 8px 32px ${color}1A` : `0 8px 32px ${color}15`,
      transition: 'all 0.3s cubic-bezier(0.4, 0, 0.2, 1)',
      animation: `slideUp 0.5s ease-out ${delay}s both`,
      '@keyframes slideUp': {
        from: { opacity: 0, transform: 'translateY(20px)' },
        to: { opacity: 1, transform: 'translateY(0)' }
      },
      '&:hover': {
        transform: 'translateY(-4px)',
        boxShadow: mode === 'dark' ? `0 12px 40px ${color}33` : `0 12px 40px ${color}25`,
        borderColor: `${color}40`,
      }
    }}>
      <Box sx={{ display: 'flex', justifyContent: 'space-between', mb: 3 }}>
        <Box>
          <Typography variant="overline" sx={{ color: 'text.secondary', fontWeight: 700, letterSpacing: 1 }}>{title}</Typography>
          <Typography variant="h4" sx={{ fontWeight: 800, mt: 0.5, color: 'text.primary' }}>{value}</Typography>
        </Box>
        <Avatar sx={{ bgcolor: `${color}15`, color: color, width: 48, height: 48, borderRadius: 3 }}>
          {icon}
        </Avatar>
      </Box>
      <Box sx={{ display: 'flex', alignItems: 'center', gap: 1 }}>
        <Box sx={{ display: 'flex', alignItems: 'center', bgcolor: trend === 'up' ? 'success.main' : 'error.main', color: '#fff', px: 0.8, py: 0.2, borderRadius: 1 }}>
          {trend === 'up' ? <TrendingUp sx={{ fontSize: 14, mr: 0.5 }} /> : <TrendingDown sx={{ fontSize: 14, mr: 0.5 }} />}
          <Typography variant="caption" sx={{ fontWeight: 700 }}>
            {trendValue}
          </Typography>
        </Box>
        <Typography variant="caption" sx={{ color: 'text.secondary', fontWeight: 500 }}>vs last month</Typography>
      </Box>
    </Card>
  );
};

export default function DashboardPage() {
  const { mode, primaryColor } = useAppTheme();
  const theme = useTheme();
  const [loading, setLoading] = useState(true);
  const [data, setData] = useState<DashboardDataDTO | null>(null);

  const fetchDashboardData = async () => {
    setLoading(true);
    try {
      const response = await axiosInstance.get('/api/Dashboard/1');
      setData(response.data);
    } catch (error) {
      console.error("Failed to fetch dashboard data, using mock", error);
      // Removed extensive mock since user has data now, but keep empty state fallback
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => {
    fetchDashboardData();
  }, []);

  if (loading) {
    return (
      <Box sx={{ display: 'flex', height: '80vh', alignItems: 'center', justifyContent: 'center' }}>
        <CircularProgress size={60} thickness={4} sx={{ color: primaryColor }} />
      </Box>
    );
  }

  const formatCurrency = (val: number) => {
    if (!val) return '$0';
    if (val >= 1000000) return `$${(val / 1000000).toFixed(2)}M`;
    if (val >= 1000) return `$${(val / 1000).toFixed(1)}k`;
    return `$${val}`;
  };

  // Extract variables
  const pieColors = ['#f59e0b', '#3b82f6', '#10b981', '#ef4444', '#8b5cf6', '#ec4899'];
  const chartData = data?.volumeTrend || [];
  
  const mappedPieData = (data?.statusDistribution || []).map((item, index) => ({
    name: item.categoryName,
    value: item.count,
    color: pieColors[index % pieColors.length]
  }));

  const radarData = data?.operationalHealth || [];
  const scatterData = data?.responseIntegrity || [];
  const barData = data?.efficiencyVelocity || [];
  const recentItems = data?.recentItems || [];
  
  const sourcePieData = (data?.sourceDistribution || []).map((item, index) => ({
    name: item.categoryName,
    value: item.count,
    color: ['#3b82f6', '#10b981', '#f59e0b', '#8b5cf6'][index % 4]
  }));

  return (
    <Box sx={{ p: { xs: 2, md: 3 }, maxWidth: 1600, margin: '0 auto' }}>
      <Box sx={{ display: 'flex', justifyContent: 'space-between', alignItems: 'flex-end', mb: 4 }}>
        <Box>
          <Typography variant="h4" sx={{ fontWeight: 800, color: 'text.primary', mb: 1, letterSpacing: '-0.5px' }}>
            NEXORA Intelligence
          </Typography>
          <Typography variant="body1" sx={{ color: 'text.secondary', fontWeight: 500 }}>
            Real-time procurement & sales analytics overview.
          </Typography>
        </Box>
        <Tooltip title="Refresh Data">
          <IconButton onClick={fetchDashboardData} sx={{ bgcolor: mode === 'dark' ? 'rgba(255,255,255,0.05)' : '#fff', boxShadow: '0 2px 12px rgba(0,0,0,0.08)', borderRadius: 3, width: 44, height: 44 }}>
            <Refresh />
          </IconButton>
        </Tooltip>
      </Box>

      {/* Row 1: Key Stats */}
      <Grid container spacing={3} sx={{ mb: 4 }}>
        <Grid size={{ xs: 12, sm: 6, md: 3 }}>
          <StatCard 
            title="TOTAL REVENUE" 
            value={formatCurrency(data?.stats?.totalOrderValue || 0)}
            icon={<LocalAtm />}
            trend={data?.stats?.ordersTrend?.isUp ? "up" : "down"}
            trendValue={data?.stats?.ordersTrend?.value || "0%"}
            color="#10b981"
            delay={0.1}
          />
        </Grid>
        <Grid size={{ xs: 12, sm: 6, md: 3 }}>
          <StatCard 
            title="ACTIVE RFQs" 
            value={data?.stats?.totalRfqs || 0}
            icon={<Assignment />}
            trend={data?.stats?.rfqsTrend?.isUp ? "up" : "down"}
            trendValue={data?.stats?.rfqsTrend?.value || "0%"}
            color={primaryColor}
            delay={0.2}
          />
        </Grid>
        <Grid size={{ xs: 12, sm: 6, md: 3 }}>
          <StatCard 
            title="WIN RATIO" 
            value={`${(data?.stats?.winVolumeRatio || 0).toFixed(2)}%`}
            icon={<Speed />}
            trend="up"
            trendValue="Auto"
            color="#3b82f6"
            delay={0.3}
          />
        </Grid>
        <Grid size={{ xs: 12, sm: 6, md: 3 }}>
          <StatCard 
            title="TOTAL LEADS" 
            value={data?.stats?.totalLeads || 0}
            icon={<People />}
            trend={data?.stats?.leadsTrend?.isUp ? "up" : "down"}
            trendValue={data?.stats?.leadsTrend?.value || "0%"}
            color="#f59e0b"
            delay={0.4}
          />
        </Grid>
      </Grid>

      {/* Row 2: Charts Area & Pie */}
      <Grid container spacing={3} sx={{ mb: 4 }}>
        <Grid size={{ xs: 12, lg: 8 }}>
          <Paper sx={{ 
            p: 3, 
            borderRadius: 4, 
            bgcolor: mode === 'dark' ? 'rgba(30, 41, 59, 0.4)' : '#ffffff',
            boxShadow: mode === 'dark' ? '0 4px 20px rgba(0,0,0,0.2)' : '0 10px 40px rgba(0,0,0,0.03)',
            border: '1px solid',
            borderColor: mode === 'dark' ? 'rgba(255,255,255,0.05)' : 'rgba(0,0,0,0.05)',
            height: 420
          }}>
            <Typography variant="h6" sx={{ fontWeight: 800, mb: 3, display: 'flex', alignItems: 'center', gap: 1.5, fontSize: '1.1rem' }}>
              <Assessment sx={{ color: primaryColor }} /> Volume & Revenue Velocity
            </Typography>
            <ResponsiveContainer width="100%" height="85%">
              <AreaChart data={chartData} margin={{ top: 10, right: 30, left: 0, bottom: 0 }}>
                <defs>
                  <linearGradient id="colorValue" x1="0" y1="0" x2="0" y2="1">
                    <stop offset="5%" stopColor={primaryColor} stopOpacity={0.4}/>
                    <stop offset="95%" stopColor={primaryColor} stopOpacity={0}/>
                  </linearGradient>
                  <linearGradient id="colorLeads" x1="0" y1="0" x2="0" y2="1">
                    <stop offset="5%" stopColor="#10b981" stopOpacity={0.4}/>
                    <stop offset="95%" stopColor="#10b981" stopOpacity={0}/>
                  </linearGradient>
                </defs>
                <XAxis dataKey="month" stroke={theme.palette.text.secondary} fontSize={12} tickLine={false} axisLine={false} dy={10} />
                <YAxis stroke={theme.palette.text.secondary} fontSize={12} tickLine={false} axisLine={false} tickFormatter={(val) => val >= 1000 ? `$${val/1000}k` : `$${val}`} dx={-10} />
                <CartesianGrid strokeDasharray="3 3" stroke={mode === 'dark' ? 'rgba(255,255,255,0.05)' : 'rgba(0,0,0,0.05)'} vertical={false} />
                <RechartsTooltip 
                  contentStyle={{ backgroundColor: mode === 'dark' ? '#0f172a' : '#fff', borderRadius: 12, border: '1px solid', borderColor: mode === 'dark' ? '#334155' : '#e2e8f0', boxShadow: '0 10px 25px rgba(0,0,0,0.1)' }}
                  itemStyle={{ fontWeight: 600 }}
                />
                <Area type="monotone" dataKey="value" stroke={primaryColor} strokeWidth={3} fillOpacity={1} fill="url(#colorValue)" />
                <Area type="monotone" dataKey="count" stroke="#10b981" strokeWidth={3} fillOpacity={1} fill="url(#colorLeads)" />
              </AreaChart>
            </ResponsiveContainer>
          </Paper>
        </Grid>

        <Grid size={{ xs: 12, lg: 4 }}>
          <Paper sx={{ 
            p: 3, 
            borderRadius: 4, 
            bgcolor: mode === 'dark' ? 'rgba(30, 41, 59, 0.4)' : '#ffffff',
            boxShadow: mode === 'dark' ? '0 4px 20px rgba(0,0,0,0.2)' : '0 10px 40px rgba(0,0,0,0.03)',
            border: '1px solid',
            borderColor: mode === 'dark' ? 'rgba(255,255,255,0.05)' : 'rgba(0,0,0,0.05)',
            height: 420,
            display: 'flex',
            flexDirection: 'column'
          }}>
            <Typography variant="h6" sx={{ fontWeight: 800, mb: 1, fontSize: '1.1rem' }}>
              RFQ Status Distribution
            </Typography>
            <Typography variant="body2" sx={{ color: 'text.secondary', mb: 3 }}>
              Distribution of current RFQs by stage.
            </Typography>
            <Box sx={{ flex: 1, position: 'relative' }}>
              <ResponsiveContainer width="100%" height="100%">
                <PieChart>
                  <Pie
                    data={mappedPieData}
                    cx="50%"
                    cy="50%"
                    innerRadius={75}
                    outerRadius={105}
                    paddingAngle={5}
                    dataKey="value"
                    stroke="none"
                  >
                    {mappedPieData.map((entry, index) => (
                      <Cell key={`cell-${index}`} fill={entry.color} />
                    ))}
                  </Pie>
                  <RechartsTooltip 
                    contentStyle={{ backgroundColor: mode === 'dark' ? '#0f172a' : '#fff', borderRadius: 12, border: '1px solid', borderColor: mode === 'dark' ? '#334155' : '#e2e8f0', boxShadow: '0 10px 25px rgba(0,0,0,0.1)' }}
                  />
                </PieChart>
              </ResponsiveContainer>
              <Box sx={{ position: 'absolute', top: '50%', left: '50%', transform: 'translate(-50%, -50%)', textAlign: 'center' }}>
                <Typography variant="h4" sx={{ fontWeight: 800 }}>{data?.stats?.totalRfqs || 0}</Typography>
                <Typography variant="caption" sx={{ color: 'text.secondary', fontWeight: 600 }}>Total RFQs</Typography>
              </Box>
            </Box>
            <Box sx={{ display: 'flex', justifyContent: 'center', gap: 3, flexWrap: 'wrap', mt: 3 }}>
              {mappedPieData.map((item) => (
                <Box key={item.name} sx={{ display: 'flex', alignItems: 'center', gap: 1 }}>
                  <Box sx={{ width: 10, height: 10, borderRadius: '50%', bgcolor: item.color }} />
                  <Typography variant="caption" sx={{ fontWeight: 600, color: 'text.primary' }}>{item.name}</Typography>
                </Box>
              ))}
            </Box>
          </Paper>
        </Grid>
      </Grid>

      {/* Row 2.5: Lead Source Analysis */}
      <Grid container spacing={3} sx={{ mb: 4 }}>
        <Grid size={{ xs: 12 }}>
          <Paper sx={{ 
            p: 3, 
            borderRadius: 4, 
            bgcolor: mode === 'dark' ? 'rgba(30, 41, 59, 0.4)' : '#ffffff',
            boxShadow: mode === 'dark' ? '0 4px 20px rgba(0,0,0,0.2)' : '0 10px 40px rgba(0,0,0,0.03)',
            border: '1px solid',
            borderColor: mode === 'dark' ? 'rgba(255,255,255,0.05)' : 'rgba(0,0,0,0.05)',
          }}>
            <Typography variant="h6" sx={{ fontWeight: 800, mb: 3, display: 'flex', alignItems: 'center', gap: 1.5, fontSize: '1.1rem' }}>
              <Speed sx={{ color: '#3b82f6' }} /> Lead Source Completeness (Email vs Manual vs Folder)
            </Typography>
            <Grid container spacing={3} sx={{ alignItems: 'center' }}>
              <Grid size={{ xs: 12, md: 5 }}>
                <Box sx={{ height: 200 }}>
                  <ResponsiveContainer width="100%" height="100%">
                    <PieChart>
                      <Pie
                        data={sourcePieData}
                        cx="50%"
                        cy="50%"
                        innerRadius={60}
                        outerRadius={80}
                        paddingAngle={5}
                        dataKey="value"
                        stroke="none"
                      >
                        {sourcePieData.map((entry, index) => (
                          <Cell key={`cell-${index}`} fill={entry.color} />
                        ))}
                      </Pie>
                      <RechartsTooltip 
                        contentStyle={{ backgroundColor: mode === 'dark' ? '#0f172a' : '#fff', borderRadius: 12, border: 'none' }}
                      />
                    </PieChart>
                  </ResponsiveContainer>
                </Box>
              </Grid>
              <Grid size={{ xs: 12, md: 7 }}>
                <Grid container spacing={2}>
                  {sourcePieData.map((item) => (
                    <Grid size={{ xs: 12, sm: 4 }} key={item.name}>
                      <Box sx={{ p: 2, borderRadius: 3, bgcolor: mode === 'dark' ? 'rgba(255,255,255,0.02)' : 'rgba(0,0,0,0.01)', border: '1px solid', borderColor: 'divider' }}>
                        <Typography variant="caption" sx={{ fontWeight: 700, color: 'text.secondary', textTransform: 'uppercase' }}>{item.name}</Typography>
                        <Typography variant="h5" sx={{ fontWeight: 800, color: item.color, mt: 0.5 }}>{item.value}</Typography>
                        <Box sx={{ width: '100%', height: 4, bgcolor: mode === 'dark' ? 'rgba(255,255,255,0.05)' : 'rgba(0,0,0,0.05)', borderRadius: 2, mt: 1, overflow: 'hidden' }}>
                          <Box sx={{ width: `${(item.value / (data?.stats?.totalLeads || 1)) * 100}%`, height: '100%', bgcolor: item.color }} />
                        </Box>
                      </Box>
                    </Grid>
                  ))}
                </Grid>
              </Grid>
            </Grid>
          </Paper>
        </Grid>
      </Grid>

      {/* Row 3: Pipeline & Efficiency */}
      <Grid container spacing={3} sx={{ mb: 4 }}>
        <Grid size={{ xs: 12, md: 6 }}>
          <Paper sx={{ p: 4, borderRadius: 4, bgcolor: mode === 'dark' ? 'rgba(30, 41, 59, 0.4)' : '#ffffff', border: '1px solid', borderColor: 'divider', height: '100%' }}>
            <Typography variant="h6" sx={{ fontWeight: 800, mb: 1, fontSize: '1.1rem' }}>
              Sales Pipeline Efficiency
            </Typography>
            <Typography variant="body2" sx={{ color: 'text.secondary', mb: 4 }}>
              Conversion rates across the primary sales funnel.
            </Typography>
            <Box sx={{ display: 'flex', flexDirection: 'column', gap: 3 }}>
              {[
                { label: 'Lead to RFQ', val: data?.stats?.conversionRates?.leadToRfq || 0, color: '#f59e0b' },
                { label: 'RFQ to Quote', val: data?.stats?.conversionRates?.rfqToQuote || 0, color: primaryColor },
                { label: 'Quote to Order', val: data?.stats?.conversionRates?.quoteToOrder || 0, color: '#10b981' },
              ].map((step, idx) => (
                <Box key={idx} sx={{ p: 2, borderRadius: 3, bgcolor: mode === 'dark' ? 'rgba(255,255,255,0.02)' : 'rgba(0,0,0,0.01)', border: '1px solid', borderColor: 'divider' }}>
                  <Box sx={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', mb: 1 }}>
                    <Typography variant="subtitle2" sx={{ fontWeight: 700, color: 'text.primary' }}>{step.label}</Typography>
                    <Typography variant="subtitle1" sx={{ fontWeight: 800, color: step.color }}>{Number(step.val).toFixed(2)}%</Typography>
                  </Box>
                  <Box sx={{ width: '100%', height: 8, bgcolor: mode === 'dark' ? 'rgba(255,255,255,0.05)' : 'rgba(0,0,0,0.05)', borderRadius: 4, overflow: 'hidden' }}>
                    <Box sx={{ width: `${Math.min(step.val, 100)}%`, height: '100%', bgcolor: step.color, borderRadius: 4, transition: 'width 1s ease-in-out' }} />
                  </Box>
                </Box>
              ))}
            </Box>
          </Paper>
        </Grid>
        
        <Grid size={{ xs: 12, md: 6 }}>
           <Paper sx={{ p: 3, borderRadius: 4, bgcolor: mode === 'dark' ? 'rgba(30, 41, 59, 0.4)' : '#ffffff', border: '1px solid', borderColor: 'divider', height: '100%' }}>
            <Typography variant="h6" sx={{ fontWeight: 800, mb: 1, fontSize: '1.1rem' }}>
              Category Efficiency
            </Typography>
            <Typography variant="body2" sx={{ color: 'text.secondary', mb: 3 }}>
              Volume breakdown by product category.
            </Typography>
            <Box sx={{ height: 280 }}>
              <ResponsiveContainer width="100%" height="100%">
                <BarChart data={barData} layout="vertical" margin={{ top: 5, right: 30, left: 20, bottom: 5 }}>
                  <CartesianGrid strokeDasharray="3 3" horizontal={true} vertical={false} stroke={mode === 'dark' ? 'rgba(255,255,255,0.05)' : 'rgba(0,0,0,0.05)'} />
                  <XAxis type="number" stroke={theme.palette.text.secondary} />
                  <YAxis dataKey="categoryName" type="category" width={80} stroke={theme.palette.text.secondary} fontSize={12} tickLine={false} axisLine={false} />
                  <RechartsTooltip 
                    cursor={{ fill: mode === 'dark' ? 'rgba(255,255,255,0.05)' : 'rgba(0,0,0,0.02)' }}
                    contentStyle={{ backgroundColor: mode === 'dark' ? '#0f172a' : '#fff', borderRadius: 8, border: '1px solid', borderColor: mode === 'dark' ? '#334155' : '#e2e8f0' }}
                  />
                  <Bar dataKey="count" fill={primaryColor} radius={[0, 4, 4, 0]} barSize={20} />
                </BarChart>
              </ResponsiveContainer>
            </Box>
          </Paper>
        </Grid>
      </Grid>

      {/* Row 4: Radar & Scatter */}
      <Grid container spacing={3} sx={{ mb: 4 }}>
        <Grid size={{ xs: 12, md: 5 }}>
          <Paper sx={{ p: 3, borderRadius: 4, bgcolor: mode === 'dark' ? 'rgba(30, 41, 59, 0.4)' : '#ffffff', border: '1px solid', borderColor: 'divider', height: 400 }}>
            <Typography variant="h6" sx={{ fontWeight: 800, mb: 1, fontSize: '1.1rem', display: 'flex', alignItems: 'center', gap: 1 }}>
              <HealthAndSafety sx={{ color: '#8b5cf6' }} /> Operational Health
            </Typography>
            <ResponsiveContainer width="100%" height="90%">
              <RadarChart cx="50%" cy="50%" outerRadius="70%" data={radarData}>
                <PolarGrid stroke={mode === 'dark' ? 'rgba(255,255,255,0.1)' : 'rgba(0,0,0,0.1)'} />
                <PolarAngleAxis dataKey="subject" tick={{ fill: theme.palette.text.secondary, fontSize: 11 }} />
                <PolarRadiusAxis angle={30} domain={[0, 100]} tick={false} axisLine={false} />
                <Radar name="Current" dataKey="a" stroke={primaryColor} fill={primaryColor} fillOpacity={0.5} />
                <Radar name="Target" dataKey="b" stroke="#8b5cf6" fill="#8b5cf6" fillOpacity={0.3} />
                <RechartsTooltip contentStyle={{ backgroundColor: mode === 'dark' ? '#0f172a' : '#fff', borderRadius: 8, border: 'none' }} />
              </RadarChart>
            </ResponsiveContainer>
          </Paper>
        </Grid>

        <Grid size={{ xs: 12, md: 7 }}>
          <Paper sx={{ p: 3, borderRadius: 4, bgcolor: mode === 'dark' ? 'rgba(30, 41, 59, 0.4)' : '#ffffff', border: '1px solid', borderColor: 'divider', height: 400 }}>
            <Typography variant="h6" sx={{ fontWeight: 800, mb: 1, fontSize: '1.1rem', display: 'flex', alignItems: 'center', gap: 1 }}>
              <ScatterPlot sx={{ color: '#ec4899' }} /> Response Integrity (Time vs Value)
            </Typography>
            <ResponsiveContainer width="100%" height="90%">
              <ScatterChart margin={{ top: 20, right: 20, bottom: 20, left: 20 }}>
                <CartesianGrid strokeDasharray="3 3" stroke={mode === 'dark' ? 'rgba(255,255,255,0.05)' : 'rgba(0,0,0,0.05)'} />
                <XAxis type="number" dataKey="x" name="Days Active" unit="d" stroke={theme.palette.text.secondary} />
                <YAxis type="number" dataKey="y" name="Value" unit="$" stroke={theme.palette.text.secondary} tickFormatter={(val) => `$${val/1000}k`} />
                <ZAxis type="number" dataKey="z" range={[50, 400]} name="Score" />
                <RechartsTooltip cursor={{ strokeDasharray: '3 3' }} contentStyle={{ backgroundColor: mode === 'dark' ? '#0f172a' : '#fff', borderRadius: 8 }} />
                <Scatter name="Items" data={scatterData} fill="#ec4899" fillOpacity={0.7} />
              </ScatterChart>
            </ResponsiveContainer>
          </Paper>
        </Grid>
      </Grid>

      {/* Row 5: Recent Items List */}
      <Grid container spacing={3}>
        <Grid size={{ xs: 12 }}>
          <Paper sx={{ p: 3, borderRadius: 4, bgcolor: mode === 'dark' ? 'rgba(30, 41, 59, 0.4)' : '#ffffff', border: '1px solid', borderColor: 'divider' }}>
            <Typography variant="h6" sx={{ fontWeight: 800, mb: 3, fontSize: '1.1rem', display: 'flex', alignItems: 'center', gap: 1 }}>
              <ListAlt sx={{ color: primaryColor }} /> Recent Activity
            </Typography>
            <TableContainer>
              <Table size="small">
                <TableHead>
                  <TableRow>
                    <TableCell sx={{ fontWeight: 700, color: 'text.secondary' }}>ID</TableCell>
                    <TableCell sx={{ fontWeight: 700, color: 'text.secondary' }}>Type</TableCell>
                    <TableCell sx={{ fontWeight: 700, color: 'text.secondary' }}>Description</TableCell>
                    <TableCell sx={{ fontWeight: 700, color: 'text.secondary' }}>Status</TableCell>
                    <TableCell sx={{ fontWeight: 700, color: 'text.secondary', textAlign: 'right' }}>Date</TableCell>
                  </TableRow>
                </TableHead>
                <TableBody>
                  {recentItems.slice(0, 10).map((item, index) => (
                    <TableRow key={index} hover>
                      <TableCell sx={{ fontWeight: 600 }}>{item.id}</TableCell>
                      <TableCell>
                        <Chip label={item.type} size="small" color={item.type === 'RFQ' ? 'primary' : 'warning'} variant="outlined" />
                      </TableCell>
                      <TableCell>{item.description}</TableCell>
                      <TableCell>
                        <Chip label={item.status} size="small" sx={{ 
                          bgcolor: item.status === 'Draft' ? 'action.hover' : 
                                  item.status === 'Submitted' ? `${primaryColor}22` : 
                                  item.status === 'Accepted' ? '#10b98122' : '#f59e0b22',
                          color: item.status === 'Draft' ? 'text.secondary' : 
                                 item.status === 'Submitted' ? primaryColor : 
                                 item.status === 'Accepted' ? '#10b981' : '#f59e0b',
                          fontWeight: 600
                        }} />
                      </TableCell>
                      <TableCell align="right" sx={{ color: 'text.secondary' }}>
                        {dayjs(item.date).format('DD MMM YYYY, HH:mm')}
                      </TableCell>
                    </TableRow>
                  ))}
                  {recentItems.length === 0 && (
                    <TableRow>
                      <TableCell colSpan={5} align="center" sx={{ py: 3, color: 'text.secondary' }}>No recent activity found.</TableCell>
                    </TableRow>
                  )}
                </TableBody>
              </Table>
            </TableContainer>
          </Paper>
        </Grid>
      </Grid>
    </Box>
  );
}
