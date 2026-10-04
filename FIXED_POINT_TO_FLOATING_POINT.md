# Fixed-Point to Floating-Point Maths

EliteSharp originally reproduced the BBC Master version's arithmetic exactly: sign-magnitude bytes, 16- and 24-bit fixed-point coordinates, and multiplication, division, square roots, sines and arctangents done through precalculated log, antilog, sine and arctan tables. Commit `68bbe44` replaced all of that with ordinary C# floating-point maths. This document records what that change does to the game, measured by running the old and new implementations side by side.

- **Baseline:** the fixed-point port, as it was before the change.
- **Float:** the floating-point implementation.
- Positions are in the original's units (the planet's radius is 24,576), and ticks are iterations of the main loop.

## Summary

| Area | Kind of difference | What changed |
|---|---|---|
| Universe generation | None | All 2,048 systems in the 8 galaxies are identical: seeds, names, coordinates, economy, government, tech level, population, productivity, radius, crater, and the planet's and sun's positions. |
| Flight and orientation | Numerical | Ships move exactly 1.5 units per unit of speed; the log tables made them about 3% slower (29 units per tick instead of 30 at speed 20). Orientations stay orthonormal to 10⁻⁷, against up to 0.1 in the original between tidies. |
| Unit vectors and the AI | Discrete | The original's NORM loses a carry in TIS2 when a vector is longer than 127, giving directions up to 44° wrong (4.5° on average). With exact vectors, ships in firing position fire 37% more often and hit us 2.3 times as often. |
| Docking | Discrete | The docking computer docks in every scenario with both implementations, but much sooner (at ticks 135, 417 and 932 against 743, 1,440 and 5,131). The safe cone of approach is now exactly 22°, which changes 326 of 18,081 docking decisions. |
| Altitude | Discrete | We crash 24,904 from the planet's centre in every direction; the original's per-axis truncation made it 25,336 to 25,848, depending on the direction. |
| Hyperspace range | None | Distances are calculated as in the original (the y difference halved and the square root rounded down before multiplying by 4), so exactly the same 16,564 pairs of systems are within 7.0 light years. Only distances over 102 light years, where the original's 16-bit sum of squares overflows, are now larger (638 pairs). |
| Rendering calculations | Numerical | Explosions take the same random numbers and last as long. Circles differ by up to 2 pixels, the planet's and sun's centres by up to 16, and the compass by up to 4. Scanner blips are identical. |
| Whole-game runs | Accumulated | The random number generator is seeded from the planet's position (x_lo), so its sequence diverges within 50 to 100 ticks, and which ships appear and how fights go diverge after that. |

## Deliberately kept

- The random number generator is still seeded from the planet's x_lo each iteration, and other sources of randomness still come from low bytes (such as z_lo for the Cougar and the enemy laser's end point).
- New ships' positions are still built from random bytes, as in the original.
- The mapping from the joystick to roll and pitch angles is unchanged, including the original's carry from the roll calculation into the pitch calculation.
- A distant explosion's counter still gets the extra 1 from the C flag, so distant explosions finish sooner.
- The planet and sun are still projected onto the original's integer screen, as the sun's fringe takes a random number for each pixel row.
- A single signed roll can't express the original's reset state, where ALPHA is positive but ALP2 (used when rotating ships' positions) is negative; this only affects the escape pod sequence. A roll of "negative zero" is also lost, which changes one case of recycling stardust in the side views.
- The docking check at TN13 reads K3+10, which the port never sets, so traders always dock once they reach the slot (as before).

## How this was measured

A temporary harness ran the same deterministic scenarios in both implementations, starting from the same states and random seeds, and compared the results. Each legacy arithmetic routine was also run over its input range against the exact operation that replaces it. The harness itself isn't kept in the repository; the tests in `tests/EliteSharp.Tests/Game` now cover the new maths, including a checksum of the whole universe as the original generates it, and every hyperspace distance against the original's routine.

## Detailed results

### 1. The legacy routines against the exact operations

Each legacy routine run over its input range (exhaustively where practical), against the exact operation that replaces it.

| Operation | Samples | Max abs error | Mean abs error | Mean signed error | % inexact | Worst case |
|---|---|---|---|---|---|---|
| FMLTU (A*Q/256, log tables) | 65536 | 6.117 | 1.188 | -1.188 | 99.2 | A=250 Q=251 -> legacy 239, exact 245.117 |
| LL28 (256*A/Q, log tables) | 32640 | 6.132 | 1.884 | -1.884 | 98.5 | A=203 Q=212 -> legacy 239, exact 245.132 |
| DVID4 (A/Q as P.R) | 65280 | 0.02395 | 0.006737 | -0.006737 | 95.5 | A=203 Q=212 -> legacy 0.933594, exact 0.957547 |
| LL5 (square root) | 65536 | 0.998 | 0.4987 | -0.4987 | 99.6 | 65535 -> legacy 255, exact 255.998 |
| DVID3B2 (256*n/d, relative) | 99999 | 1.018e+03 | 44.33 | -0.3356 | 100.0 | n=8397216 d=2110585 -> legacy 1, exact 1018.53 |
| NORM (unit vector, angle error in degrees) | 100000 | 44.39 | 4.474 | 4.474 | 99.3 | (-81, -117, -81) -> legacy 44.3941, exact 0 |
| NORM (unit vector length, 96 = 1) | 100000 | 0.5 | 0.04515 | -0.04261 | 100.0 | (-103, -68, -64) -> legacy 0.5, exact 1 |
| ARCTAN (angle, 256 = full circle) | 64770 | 1.696 | 0.5933 | 0 | 99.6 | P=-43 Q=114 -> legacy 115, exact 113.304 |
| SNE (256 * sin, table) | 32 | 1 | 0.2045 | 9.000e-06 | 96.9 | i=16 -> legacy 255, exact 256 |
| TIS2 (96*A/Q) | 16129 | 1.488 | 0.5584 | 3.084e-17 | 94.0 | A=-125 Q=127 -> legacy -93, exact -94.4882 |
| MVS4 (roll step, change in length, 96*256 = 1) | 6200 | 0.008199 | 0.001618 | 1.940e-05 | 100.0 | alpha=31 -> legacy 0.991788, exact 0.999987 |
| MVS4 (roll step, angle turned, radians) | 6200 | 0.008621 | 0.001632 | -3.209e-04 | 100.0 | alpha=31 -> legacy 0.112472, exact 0.121094 |
| MVS5 (ship turn step, change in length) | 2000 | 8.462e-05 | 2.944e-05 | 2.784e-05 | 100.0 | (-23522, 7119) -> legacy 1.00007, exact 0.999987 |
| MVS5 (ship turn step, angle turned, radians) | 2000 | 7.241e-05 | 1.999e-05 | 1.446e-05 | 100.0 | (-24529, 1521) -> legacy 0.0625724, exact 0.0625 |

### 2. Flight: movement, rotation and orientation (MVEIT, one ship, no tactics)

Position difference is the distance between the two builds' positions; nose angle is the angle between their nose vectors. Orthonormality error is the largest deviation of the orientation vectors from unit length or perpendicularity.

In the straight-line runs, the jump at tick 2000 or 5000 is the baseline wrapping around: MVEIT part 5 keeps only 16 bits of each coordinate, so a ship more than 65,535 away jumps back towards us. In the game, ships are removed from the bubble at 57,600, before this happens.

#### straight

| Tick | Baseline distance | Position diff | Relative | Nose angle diff (deg) | Roof angle diff (deg) | Ortho error baseline | Ortho error float |
|---|---|---|---|---|---|---|---|
| 0 | 2.003e+04 | 0 | 0 | 0 | 0 | 0 | 0 |
| 1 | 2.006e+04 | 1 | 4.985e-05 | 0 | 0 | 0 | 0 |
| 10 | 2.032e+04 | 10 | 4.921e-04 | 0 | 0 | 0 | 0 |
| 100 | 2.293e+04 | 100 | 0.004362 | 0 | 0 | 0 | 0 |
| 500 | 3.452e+04 | 500 | 0.01449 | 0 | 0 | 0 | 0 |
| 1000 | 4.901e+04 | 1.000e+03 | 0.0204 | 0 | 0 | 0 | 0 |
| 2000 | 1.251e+04 | 6.754e+04 | 5.397 | 0 | 0 | 0 | 0 |
| 5000 | 3.395e+04 | 1.361e+05 | 4.008 | 0 | 0 | 0 | 0 |

#### straight-diagonal

| Tick | Baseline distance | Position diff | Relative | Nose angle diff (deg) | Roof angle diff (deg) | Ortho error baseline | Ortho error float |
|---|---|---|---|---|---|---|---|
| 0 | 6.756e+03 | 0 | 0 | 7.921e-04 | 0.001131 | 3.368e-05 | 1.192e-07 |
| 1 | 6.772e+03 | 1.807 | 2.668e-04 | 0.4411 | 0.1451 | 0.03742 | 2.980e-08 |
| 10 | 6.927e+03 | 18.07 | 0.002609 | 0.4411 | 0.1451 | 0.03742 | 2.980e-08 |
| 100 | 8.972e+03 | 180.7 | 0.02014 | 0.4411 | 0.1451 | 0.03742 | 2.980e-08 |
| 500 | 2.179e+04 | 903.4 | 0.04146 | 0.4411 | 0.1451 | 0.03742 | 2.980e-08 |
| 1000 | 3.925e+04 | 1.807e+03 | 0.04604 | 0.4411 | 0.1451 | 0.03742 | 2.980e-08 |
| 2000 | 7.473e+04 | 3.614e+03 | 0.04836 | 0.4411 | 0.1451 | 0.03742 | 2.980e-08 |
| 5000 | 5.324e+04 | 1.553e+05 | 2.917 | 0.4411 | 0.1451 | 0.03742 | 2.980e-08 |

#### our-speed

| Tick | Baseline distance | Position diff | Relative | Nose angle diff (deg) | Roof angle diff (deg) | Ortho error baseline | Ortho error float |
|---|---|---|---|---|---|---|---|
| 0 | 6.001e+04 | 0 | 0 | 0 | 0 | 0 | 0 |
| 1 | 5.998e+04 | 0 | 0 | 0 | 0 | 0 | 0 |
| 10 | 5.971e+04 | 0 | 0 | 0 | 0 | 0 | 0 |
| 100 | 5.701e+04 | 0 | 0 | 0 | 0 | 0 | 0 |
| 500 | 4.501e+04 | 0 | 0 | 0 | 0 | 0 | 0 |
| 1000 | 3.002e+04 | 0 | 0 | 0 | 0 | 0 | 0 |
| 2000 | 1.118e+03 | 0 | 0 | 0 | 0 | 0 | 0 |
| 5000 | 2.449e+04 | 6.554e+04 | 2.676 | 0 | 0 | 0 | 0 |

#### roll

| Tick | Baseline distance | Position diff | Relative | Nose angle diff (deg) | Roof angle diff (deg) | Ortho error baseline | Ortho error float |
|---|---|---|---|---|---|---|---|
| 0 | 1.025e+04 | 0 | 0 | 0 | 0 | 0 | 0 |
| 1 | 1.025e+04 | 1.176 | 1.147e-04 | 0 | 1.422e-04 | 1.907e-04 | 0 |
| 10 | 1.025e+04 | 10.77 | 0.001051 | 0 | 0.08858 | 0.002692 | 1.192e-07 |
| 100 | 1.024e+04 | 48.05 | 0.004691 | 0 | 1.648 | 0.02898 | 0 |
| 500 | 1.025e+04 | 312.4 | 0.03049 | 0 | 2.962 | 0.04418 | 2.980e-08 |
| 1000 | 1.024e+04 | 610.8 | 0.05963 | 0 | 0.1969 | 0.01055 | 1.192e-07 |
| 2000 | 1.024e+04 | 1.201e+03 | 0.1173 | 0 | 5.451 | 0.03299 | 1.788e-07 |
| 5000 | 1.024e+04 | 2.795e+03 | 0.2729 | 0 | 23.57 | 0.01055 | 1.192e-07 |

#### pitch

| Tick | Baseline distance | Position diff | Relative | Nose angle diff (deg) | Roof angle diff (deg) | Ortho error baseline | Ortho error float |
|---|---|---|---|---|---|---|---|
| 0 | 1.025e+04 | 0 | 0 | 0 | 0 | 0 | 0 |
| 1 | 1.025e+04 | 0.2555 | 2.494e-05 | 5.123e-05 | 3.074e-05 | 6.866e-05 | 0 |
| 10 | 1.025e+04 | 6.615 | 6.454e-04 | 0.04661 | 0.05553 | 0.001138 | 5.960e-08 |
| 100 | 1.028e+04 | 118 | 0.01148 | 1.562 | 0.5218 | 0.0182 | 5.960e-08 |
| 500 | 1.024e+04 | 340 | 0.0332 | 4.742 | 4.716 | 0.03125 | 2.980e-08 |
| 1000 | 1.025e+04 | 684.6 | 0.06682 | 10.11 | 10.59 | 0.03125 | 1.192e-07 |
| 2000 | 1.028e+04 | 1.351e+03 | 0.1314 | 20.34 | 20.44 | 0.03125 | 1.192e-07 |
| 5000 | 1.024e+04 | 3.258e+03 | 0.3181 | 48.92 | 47.9 | 0.01788 | 5.960e-08 |

#### roll-max

| Tick | Baseline distance | Position diff | Relative | Nose angle diff (deg) | Roof angle diff (deg) | Ortho error baseline | Ortho error float |
|---|---|---|---|---|---|---|---|
| 0 | 1.421e+04 | 0 | 0 | 0 | 0 | 0 | 0 |
| 1 | 1.419e+04 | 55.11 | 0.003884 | 0 | 0.03362 | 0.007305 | 0 |
| 10 | 1.401e+04 | 422.6 | 0.03016 | 0 | 3.373 | 0.1049 | 2.384e-07 |
| 100 | 1.425e+04 | 195.6 | 0.01373 | 0 | 4.881 | 0.04453 | 5.960e-08 |
| 500 | 1.404e+04 | 335.8 | 0.02393 | 0 | 22.16 | 0.02576 | 1.192e-07 |
| 1000 | 1.408e+04 | 389.4 | 0.02766 | 0 | 45.24 | 0.08092 | 1.192e-07 |
| 2000 | 1.416e+04 | 233.6 | 0.0165 | 0 | 89.06 | 0.1065 | 1.788e-07 |
| 5000 | 1.419e+04 | 74.02 | 0.005218 | 0 | 141.3 | 0.06703 | 1.788e-07 |

#### pitch-roll

| Tick | Baseline distance | Position diff | Relative | Nose angle diff (deg) | Roof angle diff (deg) | Ortho error baseline | Ortho error float |
|---|---|---|---|---|---|---|---|
| 0 | 1.521e+04 | 0 | 0 | 7.921e-04 | 0.001131 | 3.368e-05 | 1.192e-07 |
| 1 | 1.521e+04 | 4.764 | 3.132e-04 | 0.4335 | 0.15 | 0.03791 | 1.490e-08 |
| 10 | 1.526e+04 | 48.15 | 0.003156 | 0.3957 | 0.1958 | 0.03984 | 1.788e-07 |
| 100 | 1.586e+04 | 192.1 | 0.01211 | 0.5685 | 1.997 | 0.03538 | 8.941e-08 |
| 500 | 1.866e+04 | 1.123e+03 | 0.0602 | 5.461 | 6.639 | 0.0285 | 1.192e-07 |
| 1000 | 2.289e+04 | 2.838e+03 | 0.124 | 12.24 | 15.5 | 0.05355 | 1.788e-07 |
| 2000 | 3.304e+04 | 8.152e+03 | 0.2467 | 13.38 | 30.47 | 0.03099 | 4.470e-07 |
| 5000 | 3.475e+04 | 3.920e+04 | 1.128 | 17.31 | 54.24 | 0.04546 | 1.192e-07 |

#### own-pitch

| Tick | Baseline distance | Position diff | Relative | Nose angle diff (deg) | Roof angle diff (deg) | Ortho error baseline | Ortho error float |
|---|---|---|---|---|---|---|---|
| 0 | 5.000e+03 | 0 | 0 | 0 | 0 | 0 | 0 |
| 1 | 5.000e+03 | 0 | 0 | 0.002329 | 0.002329 | 1.907e-06 | 0 |
| 10 | 5.000e+03 | 0 | 0 | 0.02149 | 0.02149 | 3.597e-04 | 0 |
| 100 | 5.000e+03 | 0 | 0 | 0.4499 | 0.4499 | 0.03125 | 0 |
| 500 | 5.000e+03 | 0 | 0 | 1.543 | 1.508 | 0.03125 | 1.192e-07 |
| 1000 | 5.000e+03 | 0 | 0 | 1.148 | 1.148 | 0.03125 | 1.192e-07 |
| 2000 | 5.000e+03 | 0 | 0 | 1.409 | 1.577 | 0.01258 | 4.768e-07 |
| 5000 | 5.000e+03 | 0 | 0 | 6.234 | 6.234 | 0.005758 | 2.384e-07 |

#### own-pitch-roll

| Tick | Baseline distance | Position diff | Relative | Nose angle diff (deg) | Roof angle diff (deg) | Ortho error baseline | Ortho error float |
|---|---|---|---|---|---|---|---|
| 0 | 5.000e+03 | 0 | 0 | 7.921e-04 | 0.001131 | 3.368e-05 | 1.192e-07 |
| 1 | 5.014e+03 | 0.8024 | 1.600e-04 | 0.4598 | 0.1523 | 0.03712 | 1.192e-07 |
| 10 | 5.137e+03 | 8.228 | 0.001602 | 0.6008 | 0.9523 | 0.02697 | 4.768e-07 |
| 100 | 5.460e+03 | 65.39 | 0.01198 | 1.591 | 2.53 | 0.04813 | 2.384e-07 |
| 500 | 8.108e+03 | 640.2 | 0.07896 | 11.06 | 3.922 | 0.02562 | 1.192e-07 |
| 1000 | 1.313e+04 | 1.721e+03 | 0.1311 | 12.38 | 14.82 | 0.01351 | 3.576e-07 |
| 2000 | 2.441e+04 | 4.552e+03 | 0.1865 | 15.77 | 28.58 | 0.03117 | 4.470e-07 |
| 5000 | 5.988e+04 | 1.045e+04 | 0.1745 | 58.17 | 92.57 | 0.02418 | 2.384e-07 |

#### own-and-our

| Tick | Baseline distance | Position diff | Relative | Nose angle diff (deg) | Roof angle diff (deg) | Ortho error baseline | Ortho error float |
|---|---|---|---|---|---|---|---|
| 0 | 8.018e+03 | 0 | 0 | 7.921e-04 | 0.001131 | 3.368e-05 | 1.192e-07 |
| 1 | 8.030e+03 | 4.254 | 5.298e-04 | 0.4134 | 0.2497 | 0.03768 | 1.192e-07 |
| 10 | 8.114e+03 | 39.48 | 0.004866 | 0.7646 | 1.455 | 0.0328 | 2.384e-07 |
| 100 | 8.066e+03 | 161.1 | 0.01997 | 1.947 | 2.991 | 0.01953 | 3.576e-07 |
| 500 | 9.766e+03 | 918.5 | 0.09405 | 17.22 | 22.96 | 0.01999 | 2.384e-07 |
| 1000 | 1.434e+04 | 1.654e+03 | 0.1153 | 31.9 | 47.86 | 0.01022 | 2.384e-07 |
| 2000 | 2.540e+04 | 6.731e+03 | 0.265 | 41.42 | 94.75 | 0.04023 | 3.576e-07 |
| 5000 | 6.602e+04 | 5.711e+04 | 0.8651 | 29.38 | 84.16 | 0.01816 | 3.576e-07 |

#### planet

| Tick | Baseline distance | Position diff | Relative | Nose angle diff (deg) | Roof angle diff (deg) | Ortho error baseline | Ortho error float |
|---|---|---|---|---|---|---|---|
| 0 | 3.202e+05 | 0 | 0 | 0 | 0 | 0 | 0 |
| 1 | 3.202e+05 | 26.16 | 8.172e-05 | 0.001901 | 0.003653 | 1.951e-04 | 7.363e-09 |
| 10 | 3.202e+05 | 255.8 | 7.989e-04 | 0.1462 | 0.06203 | 0.0016 | 2.384e-07 |
| 100 | 3.206e+05 | 1.734e+03 | 0.005407 | 1.593 | 2.402 | 0.01047 | 2.384e-07 |
| 500 | 3.222e+05 | 7.429e+03 | 0.02306 | 10.89 | 11.06 | 0.04948 | 1.192e-07 |
| 1000 | 3.243e+05 | 1.498e+04 | 0.0462 | 24.62 | 32.36 | 0.03054 | 3.576e-07 |
| 2000 | 3.287e+05 | 3.052e+04 | 0.09285 | 37.31 | 66.03 | 0.02518 | 3.576e-07 |
| 5000 | 3.438e+05 | 7.950e+04 | 0.2312 | 73.31 | 158.2 | 0.02876 | 3.576e-07 |

#### planet-fast-roll

| Tick | Baseline distance | Position diff | Relative | Nose angle diff (deg) | Roof angle diff (deg) | Ortho error baseline | Ortho error float |
|---|---|---|---|---|---|---|---|
| 0 | 7.874e+04 | 0 | 0 | 0 | 0 | 0 | 0 |
| 1 | 7.885e+04 | 241 | 0.003056 | 0.03412 | 0.08639 | 0.007637 | 1.601e-08 |
| 10 | 7.939e+04 | 2.605e+03 | 0.03282 | 2.831 | 1.676 | 0.08187 | 3.576e-07 |
| 100 | 8.188e+04 | 7.016e+03 | 0.08568 | 7.875 | 8.899 | 0.04969 | 2.645e-07 |
| 500 | 9.947e+04 | 3.903e+04 | 0.3924 | 20.87 | 54.13 | 0.02458 | 2.384e-07 |
| 1000 | 1.357e+05 | 9.156e+04 | 0.675 | 34.96 | 110.9 | 0.1207 | 3.576e-07 |
| 2000 | 2.885e+05 | 2.745e+05 | 0.9517 | 9.941 | 152.6 | 0.08643 | 5.960e-07 |
| 5000 | 4.029e+06 | 4.102e+06 | 1.018 | 131.1 | 139.5 | 0.06268 | 4.768e-07 |

### 3. Orientation normalisation (TIDY)

500 random orientations, perturbed by up to 5% in length and 0.05 in each component, then normalised by each build.

| Measure | Max | Mean |
|---|---|---|
| Input orthonormality error | 0.1197 | 0.06592 |
| Baseline output orthonormality error | 0.06396 | 0.02914 |
| Float output orthonormality error | 1.788e-07 | 6.274e-08 |
| Nose angle between builds (deg) | 1.467 | 0.64 |
| Roof angle between builds (deg) | 11.57 | 1.709 |
| Side angle between builds (deg) | 11.97 | 2.003 |

### 4. Universe generation (must be identical)

All 8 galaxies x 256 systems.

| Output | Compared | Different |
|---|---|---|
| System seeds | 2048 | 0 |
| Galactic coordinates | 2048 | 0 |
| System name | 2048 | 0 |
| Economy, government, tech level, population, productivity | 2048 | 0 |
| Planet radius | 2048 | 0 |
| Planet type (crater or meridians) | 2048 | 0 |
| Planet position in space (SOLAR) | 2048 | 0 |
| Sun position in space (SOLAR) | 2048 | 0 |

### 5. Hyperspace distances

Every ordered pair of systems in all 8 galaxies (524,288 pairs, including each system to itself). Distances are in tenths of a light year.

The first floating-point version calculated `4 * sqrt(dx² + (dy / 2)²)` exactly. The original halves the y difference with a shift (rounding it down) and rounds the square root down before multiplying by 4, so its distances are always multiples of 0.4 light years and are usually shorter. That made 75.8% of distances differ (397,254 pairs, mostly by 0.1 to 0.5 light years) and took 452 pairs out of range of a full tank (for example, Lave to Leesti went from 3.6 to 3.8 light years).

The distance is now `4 * floor(sqrt(dx² + (|dy| >> 1)²))`, using `Math.Sqrt` on the original's integer inputs (exact, as the sum is far below 2⁵²). The original's LL5 rounds the square root down for every 16-bit value, so this matches it wherever the sum fits in 16 bits. The original also caps the sum's high byte at 255 rather than going beyond 16 bits; that isn't reproduced, as it only applies to distances of more than 102 light years, which are out of range either way.

| | First float version | Now |
|---|---|---|
| Distances that differ from the original | 397,254 (75.8%) | 638 (0.12%), all over 102 light years |
| Largest difference | 8.4 light years | 8.0 light years (over 102 light years only) |
| Pairs within 7.0 light years (original: 16,564) | 16,112 | 16,564 |
| Pairs no longer within range | 452 | 0 |
| Pairs newly within range | 0 | 0 |
| Distances that differ within 7.0 light years | not measured | 0 |

At the edge of a full tank, the original's distances step from 6.8 light years (1,588 pairs) to 7.2 (1,756 pairs), and so do the new ones; no pair is exactly 7.0 light years away in either.

The tests in `HyperspaceDistanceTests` check this against a copy of the original's routine (readdistnce with LL5): every combination of x and y differences (0 to 255 each), every pair of systems in all 8 galaxies, and LL5 itself against the rounded-down square root for all 65,536 inputs.

### 6. Explosions

A Cobra Mk III killed at each distance, and drawn until its explosion finishes.

| Case | Frames baseline | Frames float | Max cloud size diff (px) | Frames with RNG state different | Mean particle spread diff |
|---|---|---|---|---|---|
| z300-x0 | 59 | 59 | 37.25 | 0 | 2.807 |
| z300-x0.5 | 59 | 59 | 37.25 | 0 | 2.358 |
| z300-x1.5 | 59 | 59 | 37.25 | 0 | 2.441 |
| z1000-x0 | 59 | 59 | 0.848 | 0 | 1.098 |
| z1000-x0.5 | 59 | 59 | 0.848 | 0 | 0.8294 |
| z1000-x1.5 | 59 | 59 | 0.848 | 0 | 0.8474 |
| z3000-x0 | 59 | 59 | 0.9733 | 0 | 3.164 |
| z3000-x0.5 | 59 | 59 | 0.9733 | 0 | 2.297 |
| z3000-x1.5 | 59 | 59 | 0.9733 | 0 | 2.259 |
| z8000-x0 | 59 | 59 | 1.056 | 0 | 6.904 |
| z8000-x0.5 | 59 | 59 | 1.056 | 0 | 8.326 |
| z8000-x1.5 | 59 | 59 | 1.056 | 0 | 5.751 |
| z8191-x0 | 59 | 59 | 0.9385 | 0 | 6.583 |
| z8191-x0.5 | 59 | 59 | 0.9385 | 0 | 4.699 |
| z8191-x1.5 | 59 | 59 | 0.9385 | 0 | 9.93 |
| z8192-x0 | 47 | 47 | 1.024 | 0 | 9.263 |
| z8192-x0.5 | 47 | 47 | 1.024 | 0 | 6.786 |
| z8192-x1.5 | 47 | 47 | 1.024 | 0 | 9.169 |
| z8500-x0 | 47 | 47 | 1.024 | 0 | 9.453 |
| z8500-x0.5 | 47 | 47 | 1.024 | 0 | 8.691 |
| z8500-x1.5 | 47 | 47 | 1.024 | 0 | 6.953 |
| z20000-x0 | 47 | 47 | 1.024 | 0 | 36.3 |
| z20000-x0.5 | 47 | 47 | 1.024 | 0 | 30.77 |
| z20000-x1.5 | 47 | 47 | 1.024 | 0 | 24.71 |
| z50000-x0 | 47 | 47 | 1.024 | 0 | 75.18 |
| z50000-x0.5 | 47 | 47 | 1.024 | 0 | 69.02 |
| z50000-x1.5 | 47 | 47 | 1.024 | 0 | 87.07 |

### 7. Rendering and dashboard calculations

| Calculation | Compared | Different | Max difference |
|---|---|---|---|
| PROJ: planet/sun centre on the original's screen | 540 | 244 (0 on/off-screen decisions) | 16.12 px |
| PLANET: radius on screen | 540 | 120 | 1 px |
| SUN: half-width of each row (including the random fringe) | 270 suns | 32 suns (3657 rows) | 135 px |
| SUN: random number state afterwards | 270 | 24 | |
| CIRCLE2: points on circles (tunnel and charts) | 36 circles | 28 | 2 px |
| SCAN: scanner blips | 2000 | 0 (0 shown/hidden) | 0 px |
| COMPAS: compass dot | 2000 | 519 | 4 px |
| SPS1/TAS2: unit vector to the planet | 2000 | | 43.35 deg (mean 3.624) |
| HITCH: ship in the crosshairs | 5000 | 3 | |

### 8. Stardust

20 particles from the same starting positions, moved for 500 iterations in each view with constant roll, pitch and speed. Particles that leave the screen are recycled using random numbers, so once the builds recycle a particle at different times, the random number sequences (and the particles) diverge.

| Case | Mean particle diff at tick 1 (px) | at 10 | at 50 | First checkpoint with RNG different |
|---|---|---|---|---|
| view0-r0-p0-s20 | 0.1063 | 64.78 | 105.8 | 5 |
| view0-r5-p0-s12 | 0.1338 | 15.71 | 101 | 10 |
| view0-r0-p-3-s30 | 0.1964 | 92.99 | 134.6 | 5 |
| view0-r-9-p4-s40 | 0.2029 | 108.8 | 126.8 | 2 |
| view0-r31-p8-s5 | 0.189 | 25.91 | 97.25 | 5 |
| view1-r0-p0-s20 | 0.1824 | 0.5001 | 87.31 | 50 |
| view1-r5-p0-s12 | 0.1953 | 0.6667 | 28.28 | 50 |
| view1-r0-p-3-s30 | 0.2764 | 1.069 | 51.29 | 50 |
| view1-r-9-p4-s40 | 0.2813 | 0.9038 | 0.8377 | 500 |
| view1-r31-p8-s5 | 0.308 | 48.14 | 91.81 | 2 |
| view2-r0-p0-s20 | 0.2411 | 2.411 | 2.534 | 20 |
| view2-r5-p0-s12 | 0.243 | 1.875 | 66.79 | 20 |
| view2-r0-p-3-s30 | 0.3654 | 12.04 | 46.29 | 10 |
| view2-r-9-p4-s40 | 0.601 | 32.67 | 116.3 | 5 |
| view2-r31-p8-s5 | 0.1799 | 129 | 121.1 | 2 |
| view3-r0-p0-s20 | 0.2411 | 11.44 | 13.67 | 10 |
| view3-r5-p0-s12 | 0.2425 | 2.278 | 70.05 | 20 |
| view3-r0-p-3-s30 | 0.3658 | 29.28 | 57.99 | 10 |
| view3-r-9-p4-s40 | 0.5605 | 47.77 | 119.1 | 5 |
| view3-r31-p8-s5 | 0.2031 | 116.7 | 113.6 | 2 |

### 9. Docking checks (ISDK) and altitude

ISDK over a grid of approach angles (station nose 0-40 degrees, slot roll 0-60 degrees, planet direction 0-40 degrees): 18081 cases, 4238 dock in the baseline, 3912 in floating point, 326 decisions differ.
- nose 0 deg, roll 0 deg, planet 24 deg: baseline docks, float fails
- nose 0 deg, roll 3 deg, planet 24 deg: baseline docks, float fails
- nose 0 deg, roll 6 deg, planet 24 deg: baseline docks, float fails
- nose 0 deg, roll 9 deg, planet 24 deg: baseline docks, float fails
- nose 0 deg, roll 12 deg, planet 24 deg: baseline docks, float fails
- nose 0 deg, roll 15 deg, planet 24 deg: baseline docks, float fails
- nose 0 deg, roll 18 deg, planet 24 deg: baseline docks, float fails
- nose 0 deg, roll 21 deg, planet 24 deg: baseline docks, float fails
- nose 0 deg, roll 24 deg, planet 24 deg: baseline docks, float fails
- nose 0 deg, roll 27 deg, planet 24 deg: baseline docks, float fails

Altimeter over 303 distances from 24,600 to 70,000 in random directions: 4 differ in whether we crash; of the rest, 265 readings differ, max 20, mean 2.375 (the altimeter reads 0-255). Cabin temperature over 303 sun distances: 293 differ, max 4.
- distance 25050: baseline crash, float 10
- distance 25200: baseline crash, float 14
- distance 25350: baseline crash, float 18
- distance 25500: baseline crash, float 21
- distance 25650: baseline 16, float 23
- distance 25800: baseline 22, float 26

The distance from the planet's centre at which we crash (the largest distance, in steps of 16, at which the altitude check kills us):

| Direction | Baseline | Float |
|---|---|---|
| (0.00, 0.00, 1.00) | 25336 | 24904 |
| (1.00, 0.00, 0.00) | 25336 | 24904 |
| (-0.00, -1.00, -0.00) | 25336 | 24904 |
| (0.71, 0.71, 0.00) | 25336 | 24904 |
| (0.58, 0.58, 0.58) | 25704 | 24904 |
| (0.27, -0.53, 0.80) | 25848 | 24904 |
| (-0.91, 0.18, 0.37) | 25512 | 24904 |
- sun distance 27140: baseline temperature 244, float 242
- sun distance 27275: baseline temperature 242, float 241
- sun distance 27410: baseline temperature 242, float 241
- sun distance 27545: baseline temperature 241, float 240

### 9a. AI: single TACTICS decisions

20000 ship states (a hostile Viper within 9,000 of us, half of them pointing roughly at us), each put through one call of TACTICS with the same random numbers in both builds.

| Decision | Different | % |
|---|---|---|
| pitch counter | 1693 | 8.5 |
| roll counter | 2275 | 11.4 |
| acceleration | 682 | 3.4 |
| firing | 2129 | 10.6 |
| our shields afterwards | 2437 | 12.2 |

Firing: baseline 5760, float 7877. Hitting us: baseline 1817, float 4204.

Firing by the true angle between the ship's nose and the direction to us (the float build fires within acos(32/36) = 27.3 degrees, and hits within acos(35/36) = 13.5 degrees):

| Angle (deg) | States | Baseline fires | Float fires |
|---|---|---|---|
| 0-5 | 932 | 534.0 | 721.0 |
| 5-10 | 2449 | 1399.0 | 1823.0 |
| 10-15 | 3212 | 1770.0 | 2389.0 |
| 15-20 | 2708 | 1473.0 | 2028.0 |
| 20-25 | 1082 | 543.0 | 794.0 |
| 25-30 | 304 | 40.0 | 122.0 |
| 30-35 | 253 | 0.0 | 0.0 |
| 35-40 | 245 | 1.0 | 0.0 |
| 40-45 | 282 | 0.0 | 0.0 |
| 45-50 | 305 | 0.0 | 0.0 |
| 50-55 | 328 | 0.0 | 0.0 |
| 55-60 | 341 | 0.0 | 0.0 |
| 60-65 | 368 | 0.0 | 0.0 |

### 10. AI: combat

Hostile ships attacking us (with us flying straight at a constant speed), through the whole main loop (tactics, lasers, missiles, spawning), for up to 3,000 iterations. 'Hits' are the iterations in which our shields went down.

| Case | Baseline outcome | Float outcome | First checkpoint with RNG different | First checkpoint with different ships in the bubble |
|---|---|---|---|---|
| sidewinder | ran to the tick limit at tick 3001 | ran to the tick limit at tick 3001 | 50 | 550 |
| viper | ran to the tick limit at tick 3001 | ran to the tick limit at tick 3001 | 50 | 150 |
| mamba-behind | ran to the tick limit at tick 3001 | ran to the tick limit at tick 3001 | 50 | 550 |
| pack | ran to the tick limit at tick 3001 | ran to the tick limit at tick 3001 | 50 | 400 |
| trader-and-pirate | ran to the tick limit at tick 3001 | ran to the tick limit at tick 3001 | 50 | 200 |

Position differences of the same ship (same slot and type) between the builds, at each checkpoint:

| Case | Checkpoint | Ships compared | Max diff | Mean diff |
|---|---|---|---|---|
| sidewinder | 0 | 4 | 0 | 0 |
| sidewinder | 100 | 4 | 330.6 | 82.66 |
| sidewinder | 200 | 4 | 1.801e+03 | 450.2 |
| sidewinder | 500 | 5 | 4.885e+03 | 1.646e+03 |
| sidewinder | 1000 | 4 | 3.836e+03 | 959.1 |
| sidewinder | 2000 | 3 | 8 | 2.667 |
| sidewinder | 3000 | 3 | 8 | 2.667 |
| viper | 0 | 4 | 0 | 0 |
| viper | 100 | 4 | 698.7 | 175.2 |
| viper | 200 | 4 | 1.375e+03 | 344.3 |
| viper | 500 | 5 | 4.075e+03 | 1.144e+03 |
| viper | 1000 | 5 | 4.572e+03 | 1.006e+03 |
| viper | 2000 | 5 | 9.930e+03 | 3.104e+03 |
| viper | 3000 | 5 | 6.609e+03 | 1.807e+03 |
| mamba-behind | 0 | 4 | 0 | 0 |
| mamba-behind | 100 | 4 | 109.4 | 27.84 |
| mamba-behind | 200 | 4 | 254.8 | 64.19 |
| mamba-behind | 500 | 4 | 704 | 176.5 |
| mamba-behind | 1000 | 4 | 5.637e+03 | 1.410e+03 |
| mamba-behind | 2000 | 4 | 5.895e+03 | 1.474e+03 |
| mamba-behind | 3000 | 4 | 9.051e+03 | 2.269e+03 |
| pack | 0 | 6 | 0 | 0 |
| pack | 100 | 6 | 237.7 | 87.71 |
| pack | 200 | 6 | 2.692e+03 | 727.8 |
| pack | 500 | 6 | 4.071e+03 | 1.288e+03 |
| pack | 1000 | 6 | 7.100e+03 | 2.534e+03 |
| pack | 2000 | 3 | 2 | 1 |
| pack | 3000 | 3 | 2 | 1 |
| trader-and-pirate | 0 | 5 | 0 | 0 |
| trader-and-pirate | 100 | 5 | 165.7 | 58.94 |
| trader-and-pirate | 200 | 5 | 1.100e+03 | 297.5 |
| trader-and-pirate | 500 | 5 | 1.666e+03 | 490.9 |
| trader-and-pirate | 1000 | 5 | 5.298e+03 | 1.407e+03 |
| trader-and-pirate | 2000 | 3 | 2 | 1 |
| trader-and-pirate | 3000 | 4 | 1.937e+04 | 4.843e+03 |

- sidewinder hits: baseline [605.0, 613.0, 621.0, 629.0, 637.0, 645.0, 653.0, 661.0, 669.0, 701.0, 749.0, 789.0, '...']; float [659.0, 1183.0, 1191.0, 1199.0, 1207.0, 1215.0, 1479.0, 1487.0, 1495.0, 1503.0, 1511.0]
- sidewinder result: baseline timeout; float timeout
- viper hits: baseline [151.0, 159.0, 183.0, 191.0, 199.0, 207.0, 215.0, 231.0, 239.0, 479.0, 487.0, 495.0, '...']; float [1319.0, 1327.0, 1335.0, 1343.0, 1351.0, 2239.0, 2247.0, 2255.0, 2263.0, 2271.0, 2279.0]
- viper result: baseline timeout; float timeout
- mamba-behind hits: baseline []; float []
- mamba-behind result: baseline timeout; float timeout
- pack hits: baseline []; float []
- pack result: baseline timeout; float timeout
- trader-and-pirate hits: baseline []; float []
- trader-and-pirate result: baseline timeout; float timeout

### 11. AI: our missile against a target

We fire a missile at a trader; the scenario stops when the target is killed or leaves the bubble.

| Case | Baseline outcome | Float outcome | First checkpoint with RNG different | First checkpoint with different ships in the bubble |
|---|---|---|---|---|
| near | stopped (condition met) at tick 123 | stopped (condition met) at tick 116 | 10 | never |
| far | stopped (condition met) at tick 1362 | stopped (condition met) at tick 269 | 10 | 260 |
| crossing | stopped (condition met) at tick 1679 | stopped (condition met) at tick 1599 | 10 | 260 |

Position differences of the same ship (same slot and type) between the builds, at each checkpoint:

| Case | Checkpoint | Ships compared | Max diff | Mean diff |
|---|---|---|---|---|
| near | 0 | 5 | 0 | 0 |
| near | 100 | 5 | 281.7 | 89.43 |
| far | 0 | 5 | 0 | 0 |
| far | 100 | 5 | 662.5 | 228.6 |
| far | 200 | 5 | 1.991e+03 | 496.2 |
| crossing | 0 | 5 | 0 | 0 |
| crossing | 100 | 5 | 355.5 | 99.75 |
| crossing | 200 | 5 | 419.9 | 118.2 |
| crossing | 500 | 5 | 2.633e+03 | 975.1 |
| crossing | 1000 | 6 | 1.442e+04 | 3.483e+03 |

- near killed: baseline [1.0]; float [1.0]
- far killed: baseline [0.0]; float [1.0]
- crossing killed: baseline [0.0]; float [0.0]

### 12. Docking: a trader docking with the station

A trader with the docking flag set, starting near the station; stops when it docks (leaves the bubble).

| Case | Baseline outcome | Float outcome | First checkpoint with RNG different | First checkpoint with different ships in the bubble |
|---|---|---|---|---|
| front | stopped (condition met) at tick 191 | stopped (condition met) at tick 799 | 100 | never |
| side | stopped (condition met) at tick 335 | stopped (condition met) at tick 327 | 100 | 100 |
| behind | stopped (condition met) at tick 487 | stopped (condition met) at tick 487 | 100 | 400 |

Position differences of the same ship (same slot and type) between the builds, at each checkpoint:

| Case | Checkpoint | Ships compared | Max diff | Mean diff |
|---|---|---|---|---|
| front | 0 | 4 | 0 | 0 |
| front | 100 | 4 | 273.8 | 68.45 |
| side | 0 | 4 | 0 | 0 |
| side | 100 | 4 | 27.5 | 6.876 |
| side | 200 | 4 | 100.9 | 25.23 |
| side | 300 | 4 | 180.9 | 45.23 |
| behind | 0 | 4 | 0 | 0 |
| behind | 100 | 4 | 61.2 | 15.3 |
| behind | 200 | 4 | 219 | 54.74 |
| behind | 400 | 4 | 160.3 | 40.07 |

### 13. Docking: our docking computer

From Lave's station, flying away at speed 20, then engaging the docking computer after 0, 150 or 400 iterations.

| Case | Baseline outcome | Float outcome | First checkpoint with RNG different | First checkpoint with different ships in the bubble |
|---|---|---|---|---|
| launch | docked (the game jumped to the docking bay) at tick 743 | docked (the game jumped to the docking bay) at tick 135 | never | never |
| away-150 | docked (the game jumped to the docking bay) at tick 1440 | docked (the game jumped to the docking bay) at tick 417 | 200 | 400 |
| away-400 | docked (the game jumped to the docking bay) at tick 5131 | docked (the game jumped to the docking bay) at tick 932 | 200 | 200 |

Position differences of the same ship (same slot and type) between the builds, at each checkpoint:

| Case | Checkpoint | Ships compared | Max diff | Mean diff |
|---|---|---|---|---|
| launch | 0 | 3 | 0 | 0 |
| away-150 | 0 | 3 | 0 | 0 |
| away-150 | 200 | 4 | 1.780e+03 | 602.2 |
| away-400 | 0 | 3 | 0 | 0 |
| away-400 | 200 | 3 | 10 | 3.333 |

### 14. Altitude: flying into the planet

Flying at the planet until we crash; the altitudes list is (tick, reading) each time the altimeter changes.

| Case | Baseline outcome | Float outcome | First checkpoint with RNG different | First checkpoint with different ships in the bubble |
|---|---|---|---|---|
| straight | died (DEATH2) at tick 2006 | died (DEATH2) at tick 2006 | 100 | 200 |
| offset | died (DEATH2) at tick 1430 | died (DEATH2) at tick 1462 | 100 | 300 |

Position differences of the same ship (same slot and type) between the builds, at each checkpoint:

| Case | Checkpoint | Ships compared | Max diff | Mean diff |
|---|---|---|---|---|
| straight | 0 | 3 | 0 | 0 |
| straight | 100 | 3 | 0 | 0 |
| straight | 200 | 3 | 10 | 3.333 |
| straight | 500 | 4 | 1.357e+03 | 339.2 |
| straight | 1000 | 4 | 1.857e+03 | 464.2 |
| straight | 2000 | 4 | 2.857e+03 | 714.2 |
| offset | 0 | 3 | 0 | 0 |
| offset | 100 | 3 | 0 | 0 |
| offset | 200 | 3 | 0 | 0 |
| offset | 500 | 5 | 3.045e+03 | 863.1 |
| offset | 1000 | 5 | 3.472e+03 | 1.112e+03 |
| offset | 1400 | 5 | 3.851e+03 | 1.359e+03 |

- straight altitudes: baseline [0.0, 0.0, 23.0, 232.0, 55.0, 229.0, 87.0, 226.0, 119.0, 223.0, 151.0, 221.0, '...']; float [0.0, 0.0, 23.0, 232.0, 55.0, 229.0, 87.0, 227.0, 119.0, 224.0, 151.0, 221.0, '...']
- straight result: baseline RestartAfterDeath; float RestartAfterDeath
- offset altitudes: baseline [0.0, 0.0, 23.0, 236.0, 55.0, 231.0, 87.0, 227.0, 119.0, 223.0, 151.0, 219.0, '...']; float [0.0, 0.0, 23.0, 255.0, 55.0, 233.0, 87.0, 229.0, 119.0, 225.0, 151.0, 221.0, '...']
- offset result: baseline RestartAfterDeath; float RestartAfterDeath

### 15. Free flight

5,000 iterations of flight with scripted changes of roll and pitch every 40 iterations, including spawning, stardust and everything else in the main loop.

| Case | Baseline outcome | Float outcome | First checkpoint with RNG different | First checkpoint with different ships in the bubble |
|---|---|---|---|---|
| seed61 | died (DEATH2) at tick 2262 | died (DEATH2) at tick 2166 | 100 | 200 |
| seed62 | ran to the tick limit at tick 5001 | died (DEATH2) at tick 3382 | 100 | 900 |

Position differences of the same ship (same slot and type) between the builds, at each checkpoint:

| Case | Checkpoint | Ships compared | Max diff | Mean diff |
|---|---|---|---|---|
| seed61 | 0 | 3 | 0 | 0 |
| seed61 | 100 | 3 | 169.5 | 59.2 |
| seed61 | 200 | 3 | 576.8 | 200.2 |
| seed61 | 500 | 3 | 616.2 | 223.8 |
| seed61 | 1000 | 3 | 498.8 | 202.8 |
| seed61 | 2000 | 3 | 1.366e+03 | 652.6 |
| seed62 | 0 | 3 | 0 | 0 |
| seed62 | 100 | 3 | 831 | 293.2 |
| seed62 | 200 | 3 | 1.383e+03 | 478.9 |
| seed62 | 500 | 3 | 1.417e+03 | 506.3 |
| seed62 | 1000 | 3 | 2.241e+03 | 824.7 |
| seed62 | 2000 | 3 | 3.819e+03 | 1.657e+03 |
| seed62 | 3000 | 3 | 6.056e+03 | 3.181e+03 |
