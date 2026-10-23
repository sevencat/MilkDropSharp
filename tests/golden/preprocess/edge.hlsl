#define MUL(a,b) ((a)*(b))
#define CALL(f, x) f(x)
#define NEST(x) MUL(x, (1+2))
#define SELF SELF
float x = MUL((1+2), CALL(abs, -3));
float y = NEST(4);
float z = SELF;
int w = MUL(1,2) + 3;
