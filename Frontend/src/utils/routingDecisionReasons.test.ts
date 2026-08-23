import { describe, expect, it } from 'vitest';
import { routingDecisionSentence } from './routingDecisionReasons';

describe('routingDecisionSentence', () => {
  it('turns every code the routing engine emits into a sentence', () => {
    // The complete set from DeterministicRoutingEngine plus the manual-assignment code written by
    // CommercialRoutingApplicationService. If the engine gains a code, this list must gain it too.
    const codes = [
      'PRIMARY_OWNER_ASSIGNED', 'BACKUP_OWNER_ASSIGNED', 'BACKUP_OWNER_ASSIGNED_FOR_WORKLOAD',
      'NO_MATCH_EVIDENCE', 'MATCH_BELOW_THRESHOLD', 'AMBIGUOUS_CUSTOMER',
      'NO_EFFECTIVE_OWNERSHIP', 'OWNER_UNAVAILABLE', 'MANUAL_ASSIGNMENT',
    ];
    for (const code of codes) {
      const sentence = routingDecisionSentence(code);
      expect(sentence).not.toBe(code);
      expect(sentence).toMatch(/\.$/);
    }
  });

  it('returns an unknown code unchanged rather than hiding it', () => {
    // A code added to the engine later must show up as itself, not vanish from the lead record.
    expect(routingDecisionSentence('SOME_FUTURE_CODE')).toBe('SOME_FUTURE_CODE');
  });

  it('renders a lead with no recorded reason as empty rather than as "undefined"', () => {
    expect(routingDecisionSentence(null)).toBe('');
    expect(routingDecisionSentence(undefined)).toBe('');
  });
});
