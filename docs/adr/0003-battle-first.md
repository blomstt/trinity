# 3. First milestone is a playable battle

Date: 2026-10-17. Status: accepted.

## Context

Porting Varendal could start from the campaign map, from battles, or from engine infrastructure with no gameplay.

## Decision

The first milestone (M1) is a playable battle: the hero's melee inside a large army battle on real terrain, with orders, morale and a HUD.

## Consequences

- Battles are why the move happened, and the prototype already covers the crowd simulation and rendering.
- M1 pulls in the renderer, animation, terrain, physics and UI early; navigation and most game screens wait for M2 and M3.
- Each engine system is built to the depth a battle needs, then extended by later milestones.
