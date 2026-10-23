#define A 1
#define B A
#define ADD(x,y) ((x)+(y))
#define WRAP(v) ADD(v, 1)
#define ID(x) x
#define ENTRY main
#if 1
float a = WRAP(2);
#else
float dead = 1.0;
#endif
#if 0
float alsoDead = 2.0;
#else
float b = ADD(3, 4);
#endif
float c = ID(a);
