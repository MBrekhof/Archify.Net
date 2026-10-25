# Renting in a big city for the first time: what should you confirm before signing? {#first-rent}

It comes down to three things: **confirm the person, the flat and the money**. Verify the landlord's identity and ownership, write the key terms into the contract, and keep evidence of every payment and the handover.

- Check the landlord's identity and ownership before signing; do not rely on verbal promises.
- Rent, deposit, term, penalties and repair duties must be in the contract.
- Pay in a way that leaves a record, and inspect and photograph every item at handover.

@answer scope="Cities in mainland China · whole or shared flat in an ordinary home · first lease"
@meta locale=en

## In what order do you check before signing? {#flow}

@workflow id=rent-flow title="Checks before signing"
| lane verify | Verify |
| lane paper | Contract and money |
| lane move | Handover |
| see | View the flat | external | verify |
| id | Check identity and ownership | backend | verify |
| ok | Signer verified? | backend | verify |
| walk | Walk away | external | verify |
| terms | Write terms into the contract | backend | paper |
| pay | Pay and keep receipts | backend | paper |
| check | Inspect and photograph | backend | move |
| see > id | |
| id > ok | |
| ok > walk | No | dashed |
| ok > terms | Yes | emphasis |
| terms > pay | |
| pay > check | |

@steps id=rent-steps
| see | View the flat | Visit once in the evening and once at the weekend; test light, noise, water pressure and phone signal; check the commute. | rent-flow | see |
| id | Check identity and ownership | See the original ID card and the property certificate; confirm the signer is the owner, or an agent with the owner's power of attorney. | rent-flow | id |
| terms | Write terms into the contract | To avoid disputes, put the key promises into the contract or an addendum, confirmed by both sides. | rent-flow | terms |
| pay | Pay and keep receipts | Note the purpose on each transfer; ask for a receipt, or have the contract state the amount received. | rent-flow | pay |
| check | Inspect and photograph | Photograph the water, power and gas meter readings, the furniture and appliances, and any existing damage. | rent-flow | check |

## Which terms must the contract spell out? {#clauses}

| Term | What to confirm | Common trap |
| --- | --- | --- |
| Rent and payment | Amount, payment cycle, the receiving account is the landlord's own | The payee is not the landlord {bad:High risk} |
| Deposit | Amount, conditions and deadline for its return | Deductions as "cleaning" or "wear" fees {bad:High risk} |
| Term and move-out | Start and end dates, penalty for leaving early | The whole deposit kept as a penalty {caution:Watch} |
| Repairs | Who fixes and pays for appliances, pipes and wiring | Everything pushed onto the tenant {caution:Watch} |
| Subletting and renewal | Whether you may sublet, how much rent may rise on renewal | No subletting and no early exit either {caution:Watch} |
| Utilities and fees | Who pays water, power, gas, management, internet and heating | The previous tenant's arrears billed to you {caution:Watch} |

## What do you check on signing day? {#day-of}

@facts id=day-checks
| Landlord's ID card | Check the original; the name matches the property certificate |
| Property certificate | Check the address and the owner |
| Signing through an agent | Requires a power of attorney signed by the owner |
| First payment | Deposit plus the first rent period, amount written into the contract |
| Agency fee | Often half a month to a month's rent; agree it in advance (rule of thumb) |

@callout level=warning title="Walk away if you see these"
| Photos only, no viewing allowed, and pressure to pay a holding fee first.
| No proof of ownership, or a sublessor without the owner's written consent to sublet.
| Cash or a transfer to a third party's account demanded, with no receipt.
| A newly renovated flat with a sharp smell, or an obvious partitioned or overcrowded unit.

## What do you check at handover? {#handover}

- [ ] Write down the water, power and gas meter readings and photograph them
- [ ] Test the air conditioner, water heater, gas stove, washing machine and toilet drain
- [ ] Check door and window locks, water pressure and leaks
- [ ] Record existing damage to walls and floors, and send the photos to the landlord
- [ ] Count the keys and access cards
- [ ] Confirm the previous tenant's water, power, gas and management fees are settled
